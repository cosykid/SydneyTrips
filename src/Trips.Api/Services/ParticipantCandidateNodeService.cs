using NetTopologySuite.Geometries;
using Trips.Core.Abstractions;
using Trips.Core.Domain;

namespace Trips.Api.Services;

/// <summary>
/// Builds the candidate pickup-node set for a participant.
///
/// <para><b>Model.</b> Pickup hubs are transit interchanges where a driver could collect a
/// passenger en route to the trip's destination. We source them from three corridors:</para>
/// <list type="number">
///   <item>The passenger's <em>own</em> PT corridor: TfNSW plans <c>home → destination</c>
///         and every PT-leg endpoint along that plan is admitted. These cover the case where
///         the passenger PTs partway and a driver picks them up at the alighting point.</item>
///   <item>Each <em>driver's</em> meeting hubs: the PT-leg endpoints on the driver's own
///         <c>home → destination</c> plan, <em>plus</em> the transit stops near the driver's
///         origin (<see cref="ITfNswClient.CoordinateRequestAsync"/>). The coordinate-request
///         channel is the one that surfaces a station near a driver whose PT corridor is a short,
///         interchange-free hop — e.g. it puts Macquarie Park / Eastwood (near a Top Ryde driver)
///         into a Redfern passenger's options, so the passenger Metros north to meet the car
///         instead of the car detouring all the way south. For each hub we probe TfNSW for a plan
///         from the passenger's home; if the total access journey is within the cap, it joins.</item>
///   <item>A <em>fallback</em>: if the two channels above leave a passenger with nothing but
///         Home, we admit the transit stops nearest the passenger themselves. A passenger must
///         never be reducible only to a doorstep pickup — that is what forces a wasteful detour.</item>
/// </list>
///
/// <para>Home is always present so the solver can still choose doorstep pickup when that wins.
/// Hubs within <see cref="DestinationProximityMetres"/> of the trip destination are dropped —
/// those collapse "carpool" into "passenger PTs all the way", which defeats the point. The
/// <see cref="MaxAccessMins"/> cap keeps far-flung hubs out of the set; we deliberately do not cap
/// walking separately — a long walk is the objective's concern (the walking-distance weight), not
/// a hard gate, so a passenger willing to walk to a station isn't denied that option.</para>
///
/// <para><b>PT variability.</b> Plans are computed against <c>trip.DepartAt − safetyBuffer</c>
/// so the recommended hubs have slack against small delays. Time-of-day variation falls out
/// naturally because EFA plans are time-specific. Realtime disruptions are not handled here.</para>
/// </summary>
public sealed class ParticipantCandidateNodeService
{
    /// <summary>Slack between the passenger's expected hub arrival and the driver pickup time.</summary>
    private const int SafetyBufferMins = 15;

    /// <summary>Hubs within this distance of the trip destination collapse "carpool" into
    /// "PT all the way" — drop them so the solver is forced to choose a real meeting point.</summary>
    private const int DestinationProximityMetres = 800;

    /// <summary>
    /// Sanity cap on a passenger's <em>total</em> access journey to a hub — walk + PT minutes
    /// combined. Walking is just another access mode (the same way the rider experiences a PT leg),
    /// so we don't cap it separately; how much a long walk "costs" is the objective's job (the
    /// Walking-distance weight), not a hard feasibility gate that silently deletes good hubs. This
    /// cap only exists to keep genuinely absurd candidates out — e.g. a Bondi passenger seeing
    /// Hornsby as "reachable" via a two-hour journey.
    /// </summary>
    private const int MaxAccessMins = 90;

    /// <summary>Radius for the coordinate-request probe around a driver's origin. Wide enough to
    /// catch the driver's nearest few interchanges (a couple of suburbs) without dragging in the
    /// whole region.</summary>
    private const int DriverHubRadiusMetres = 3500;

    /// <summary>Radius for the per-passenger fallback coordinate request.</summary>
    private const int FallbackHubRadiusMetres = 3000;

    /// <summary>Cap on how many stops each coordinate request contributes — keeps the meeting-hub
    /// pool (and therefore the per-passenger probe count) bounded on dense parts of the network.</summary>
    private const int MaxHubsPerCoordRequest = 6;

    /// <summary>Cap on fallback hubs admitted for an otherwise-stranded passenger.</summary>
    private const int MaxFallbackHubs = 4;

    /// <summary>
    /// Ceiling on concurrent TfNSW calls. The API rate-limits per key and Polly trips a circuit
    /// breaker on a burst of 429s — which is exactly what an unbounded <c>Task.WhenAll</c> over a
    /// dozen hub probes produces, cascading every later passenger to an empty set. Two in flight
    /// keeps us comfortably under the limit while still overlapping the I/O wait. The gate is
    /// per-instance; one instance handles a whole refresh loop (see RefreshCandidateNodesAsync).
    /// </summary>
    private const int MaxConcurrentTfNswCalls = 2;

    private readonly ITfNswClient _tfnsw;
    private readonly ILogger<ParticipantCandidateNodeService> _logger;
    private readonly SemaphoreSlim _tfnswGate = new(MaxConcurrentTfNswCalls);

    // Memoised driver meeting-hub pool for the trip currently being refreshed. The pool depends
    // only on the trip's drivers + destination, so it's identical for every passenger — computing
    // it once per refresh instead of once per passenger removes most of the call volume.
    private (Guid TripId, IReadOnlyList<MeetingHub> Hubs)? _cachedPool;

    public ParticipantCandidateNodeService(
        ITfNswClient tfnsw,
        ILogger<ParticipantCandidateNodeService> logger)
    {
        ArgumentNullException.ThrowIfNull(tfnsw);
        ArgumentNullException.ThrowIfNull(logger);
        _tfnsw = tfnsw;
        _logger = logger;
    }

    public async Task PopulateAsync(Participant participant, Trip trip, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(participant);
        ArgumentNullException.ThrowIfNull(trip);

        // Home — always present so doorstep pickup remains an option for the solver.
        participant.AddCandidateNode(new CandidateNode(
            id: Guid.NewGuid(),
            participantId: participant.Id,
            kind: NodeKind.Home,
            location: participant.Home,
            walkMins: 0,
            ptMins: 0,
            displayName: "Home"));

        var planAt = trip.DepartAt - TimeSpan.FromMinutes(SafetyBufferMins);

        // `seen` tracks hubs we've already admitted so later channels don't double-add a hub that
        // an earlier channel already placed. Keyed by 4dp geo bucket (~11m).
        var seen = new Dictionary<string, byte>();

        // Channel 1: passenger's own corridor.
        var ownPlan = await SafeTripPlanAsync(participant.Home, trip.DestinationLocation, planAt, ct);
        AdmitHubsFromPlan(participant, ownPlan, trip.DestinationLocation, seen);

        // Drivers get no further channels — they aren't picked up.
        if (participant.HasCar)
        {
            return;
        }

        // Channel 2: meeting hubs near and along the drivers' routes (the key fix — these are the
        // points a *driver* will naturally pass through or can swing by, not just those the
        // passenger's own corridor happens to expose). Probe each in parallel — TfNSW calls are
        // I/O-bound and independent — then apply the results serially, because the participant's
        // candidate list and `seen` are not thread-safe.
        var meetingHubs = await GetDriverMeetingHubsAsync(trip, planAt, ct);
        var probes = await Task.WhenAll(meetingHubs.Select(async hub =>
            (hub, plan: await SafeTripPlanAsync(participant.Home, hub.Location, planAt, ct))));

        foreach (var (hub, plan) in probes)
        {
            TryAdmitProbedHub(participant, hub, plan, seen);
        }

        // Channel 3 (robustness): a passenger must never be reducible to only Home — that forces a
        // car to their doorstep, the exact wasteful detour we're trying to eliminate. If the two
        // channels above produced nothing, admit the transit stops nearest the passenger.
        if (!participant.CandidateNodes.Any(n => n.Kind != NodeKind.Home))
        {
            await AddNearbyFallbackHubsAsync(participant, trip, planAt, seen, ct);
        }

        var hubCount = participant.CandidateNodes.Count(n => n.Kind != NodeKind.Home);
        _logger.LogDebug(
            "Candidate nodes for {Participant}: {HubCount} pickup hub(s) + Home (from {PoolSize} meeting hubs probed)",
            participant.DisplayName, hubCount, meetingHubs.Count);
    }

    /// <summary>
    /// Walk a TfNSW plan's legs, accumulating walk + PT minutes, and admit each PT-leg endpoint
    /// as a candidate node (filtered by the total-access cap + destination proximity). The
    /// <see cref="CandidateNode.Path"/> is built from the prefix of legs up to and including
    /// the admitted endpoint, so the FE can render the real PT route from home to that hub.
    /// </summary>
    private static void AdmitHubsFromPlan(
        Participant participant,
        TfNswTripPlan? plan,
        Point tripDestination,
        Dictionary<string, byte> seen)
    {
        if (plan is null || plan.Legs.Count == 0) return;

        var totalWalk = 0;
        var totalPt = 0;
        for (var i = 0; i < plan.Legs.Count; i++)
        {
            var leg = plan.Legs[i];
            var isWalk = string.Equals(leg.Mode, "walk", StringComparison.OrdinalIgnoreCase);
            if (isWalk) totalWalk += leg.DurationMins; else totalPt += leg.DurationMins;
            if (isWalk) continue;
            if (leg.To is null) continue;
            if (totalWalk + totalPt > MaxAccessMins) continue;
            if (IsNearDestination(leg.To, tripDestination)) continue;

            var key = GeoBucketKey(leg.To);
            if (!seen.TryAdd(key, 0)) continue;

            var path = BuildPathLineString(plan.Legs, prefixLength: i + 1);
            var pathLegs = BuildPathLegs(plan.Legs, prefixLength: i + 1);

            participant.AddCandidateNode(new CandidateNode(
                id: Guid.NewGuid(),
                participantId: participant.Id,
                kind: ClassifyStopKind(leg.Mode),
                location: leg.To,
                walkMins: totalWalk,
                ptMins: totalPt,
                displayName: leg.ToName ?? $"{leg.Mode} stop",
                path: path,
                pathLegs: pathLegs));
        }
    }

    /// <summary>
    /// Admit a hub the passenger was probed against, if the probe plan is within the total-access
    /// cap. Used by both the driver-meeting-hub channel and the fallback channel — walking and PT
    /// count the same toward the cap, so a hub a passenger can simply walk to is just as admissible
    /// as one they ride to.
    /// </summary>
    private void TryAdmitProbedHub(
        Participant participant,
        MeetingHub hub,
        TfNswTripPlan? probe,
        Dictionary<string, byte> seen)
    {
        if (probe is null) return;
        // A zero-minute plan means TfNSW produced no journey — a past/invalid departure date, a
        // rate-limited call, or genuinely no route. MapTripPlan surfaces that as a *non-null* plan
        // with no legs and zero minutes (you can't reach a distinct hub in 0 minutes, so a real
        // plan always totals > 0). Admitting it fabricates a free, geometry-less "teleport" pickup:
        // the solver sees a zero-cost hub and the map can only draw it as a crow-fly line. Treat it
        // like a null probe and skip the hub — the passenger keeps Home (and Channel 3 can still
        // try) rather than gaining a phantom pickup.
        if (probe.TotalWalkMins + probe.TotalPtMins <= 0) return;
        if (probe.TotalWalkMins + probe.TotalPtMins > MaxAccessMins) return;

        var key = GeoBucketKey(hub.Location);
        if (!seen.TryAdd(key, 0)) return;

        // The probe plan IS the home→hub journey, so its legs in full form the path; truncating
        // would clip off any final walk that's part of reaching the meeting point.
        var path = BuildPathLineString(probe.Legs, prefixLength: probe.Legs.Count);
        var pathLegs = BuildPathLegs(probe.Legs, prefixLength: probe.Legs.Count);

        participant.AddCandidateNode(new CandidateNode(
            id: Guid.NewGuid(),
            participantId: participant.Id,
            kind: hub.Kind,
            location: hub.Location,
            walkMins: probe.TotalWalkMins,
            ptMins: probe.TotalPtMins,
            externalId: hub.StopId,
            displayName: hub.Name,
            path: path,
            pathLegs: pathLegs));
    }

    /// <summary>Memoised accessor for the per-trip meeting-hub pool — computed on the first
    /// passenger of a refresh and reused for the rest (the pool is passenger-independent).</summary>
    private async Task<IReadOnlyList<MeetingHub>> GetDriverMeetingHubsAsync(
        Trip trip,
        DateTimeOffset planAt,
        CancellationToken ct)
    {
        if (_cachedPool is { } cached && cached.TripId == trip.Id)
        {
            return cached.Hubs;
        }

        var hubs = await CollectDriverMeetingHubsAsync(trip, planAt, ct);
        _cachedPool = (trip.Id, hubs);
        return hubs;
    }

    /// <summary>
    /// The pool of meeting hubs every driver makes available: the PT-corridor interchanges the
    /// driver naturally rolls through, plus the transit stops near each driver's origin
    /// (<see cref="ITfNswClient.CoordinateRequestAsync"/>) — the latter surface "near &lt;driver&gt;"
    /// interchanges even when the driver's PT corridor is a short, interchange-free hop. The
    /// PT-corridor endpoints already cover the "along the way" interchanges, so we don't sample
    /// extra corridor points (every probe costs a rate-limited TfNSW call). Deduplicated by geo
    /// bucket and stripped of anything hugging the destination.
    /// </summary>
    private async Task<IReadOnlyList<MeetingHub>> CollectDriverMeetingHubsAsync(
        Trip trip,
        DateTimeOffset planAt,
        CancellationToken ct)
    {
        var drivers = trip.Participants.Where(p => p.HasCar).ToList();
        if (drivers.Count == 0) return Array.Empty<MeetingHub>();

        var hubs = new Dictionary<string, MeetingHub>();
        void AddHub(MeetingHub hub)
        {
            if (IsNearDestination(hub.Location, trip.DestinationLocation)) return;
            hubs.TryAdd(GeoBucketKey(hub.Location), hub);
        }

        // (a) PT-corridor interchanges the driver naturally rolls through.
        var planTasks = drivers
            .Select(d => SafeTripPlanAsync(d.Home, trip.DestinationLocation, planAt, ct))
            .ToArray();

        // (b) transit stops near each driver's origin — the "near <driver>" meeting points.
        var coordTasks = drivers
            .Select(d => SafeCoordinateRequestAsync(d.Home, DriverHubRadiusMetres, ct))
            .ToArray();

        var plans = await Task.WhenAll(planTasks);
        foreach (var plan in plans)
        {
            if (plan is null) continue;
            foreach (var leg in plan.Legs)
            {
                if (string.Equals(leg.Mode, "walk", StringComparison.OrdinalIgnoreCase)) continue;
                if (leg.To is null) continue;
                AddHub(new MeetingHub(leg.To, leg.ToName ?? $"{leg.Mode} stop", ClassifyStopKind(leg.Mode), StopId: null));
            }
        }

        var coordResults = await Task.WhenAll(coordTasks);
        foreach (var stops in coordResults)
        {
            foreach (var stop in SelectHubs(stops, MaxHubsPerCoordRequest, trip.DestinationLocation))
            {
                AddHub(new MeetingHub(stop.Location, stop.Name, ClassifyStopKind(stop.Mode), stop.StopId));
            }
        }

        return hubs.Values.ToList();
    }

    /// <summary>
    /// Fallback for a passenger left with only Home: admit the nearest transit stops to the
    /// passenger themselves. Walk-only stops are allowed here — a stop the passenger can reach on
    /// foot is still a better meeting point than their doorstep.
    /// </summary>
    private async Task AddNearbyFallbackHubsAsync(
        Participant participant,
        Trip trip,
        DateTimeOffset planAt,
        Dictionary<string, byte> seen,
        CancellationToken ct)
    {
        var stops = await SafeCoordinateRequestAsync(participant.Home, FallbackHubRadiusMetres, ct);
        var picks = SelectHubs(stops, MaxFallbackHubs, trip.DestinationLocation);

        var probes = await Task.WhenAll(picks.Select(async stop =>
            (hub: new MeetingHub(stop.Location, stop.Name, ClassifyStopKind(stop.Mode), stop.StopId),
             plan: await SafeTripPlanAsync(participant.Home, stop.Location, planAt, ct))));

        foreach (var (hub, plan) in probes)
        {
            TryAdmitProbedHub(participant, hub, plan, seen);
        }

        if (!participant.CandidateNodes.Any(n => n.Kind != NodeKind.Home))
        {
            _logger.LogWarning(
                "Participant {Participant} has no reachable pickup hub; solver will fall back to doorstep pickup",
                participant.DisplayName);
        }
    }

    /// <summary>
    /// Pick the most useful hubs out of a coordinate-request result: drop anything hugging the
    /// destination, prefer rail/metro/ferry interchanges over bus stops (better, more legible
    /// meeting points), and cap the count. Stops arrive distance-sorted from the client.
    /// </summary>
    private static IEnumerable<TfNswCoordinateStop> SelectHubs(
        IReadOnlyList<TfNswCoordinateStop> stops,
        int max,
        Point tripDestination)
        => stops
            .Where(s => !IsNearDestination(s.Location, tripDestination))
            .OrderBy(s => IsRailLike(s.Mode) ? 0 : 1)
            .ThenBy(s => s.DistanceMeters)
            .Take(max);

    /// <summary>
    /// Concatenate the first <paramref name="prefixLength"/> legs' polylines into one LineString.
    /// Returns null when no leg has geometry (stub clients, cached pre-feature plans) or when
    /// fewer than 2 points result — LineString requires ≥ 2 points.
    /// </summary>
    private static LineString? BuildPathLineString(IReadOnlyList<TfNswJourneyLeg> legs, int prefixLength)
    {
        var coords = new List<Coordinate>();
        for (var i = 0; i < prefixLength && i < legs.Count; i++)
        {
            var poly = legs[i].Polyline;
            if (poly is null) continue;
            foreach (var p in poly)
            {
                // Skip exact duplicates at leg boundaries (TfNSW often repeats the interchange
                // point in both legs).
                if (coords.Count > 0 && coords[^1].X == p.X && coords[^1].Y == p.Y) continue;
                coords.Add(new Coordinate(p.X, p.Y));
            }
        }
        if (coords.Count < 2) return null;
        var factory = new GeometryFactory(new PrecisionModel(), 4326);
        return factory.CreateLineString(coords.ToArray());
    }

    /// <summary>
    /// The first <paramref name="prefixLength"/> legs as mode-tagged segments, preserving each
    /// leg's TfNSW mode ("walk" / "train" / "bus" / "ferry" / "lightrail" / …) and geometry so the
    /// map can colour them separately. Legs without geometry are skipped; returns null when none
    /// survive (stub clients, pre-feature cached plans) — callers fall back to the flattened path.
    /// </summary>
    private static IReadOnlyList<PathLeg>? BuildPathLegs(IReadOnlyList<TfNswJourneyLeg> legs, int prefixLength)
    {
        var result = new List<PathLeg>();
        for (var i = 0; i < prefixLength && i < legs.Count; i++)
        {
            var leg = legs[i];
            var poly = leg.Polyline;
            if (poly is null || poly.Count < 2) continue;
            var points = new List<PathPoint>(poly.Count);
            foreach (var p in poly)
            {
                points.Add(new PathPoint(p.X, p.Y));
            }
            result.Add(new PathLeg(leg.Mode, points));
        }
        return result.Count > 0 ? result : null;
    }

    private async Task<TfNswTripPlan?> SafeTripPlanAsync(
        Point origin,
        Point destination,
        DateTimeOffset planAt,
        CancellationToken ct)
    {
        await _tfnswGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await _tfnsw.TripPlanAsync(origin, destination, planAt, ct).ConfigureAwait(false);
        }
        catch (NotImplementedException)
        {
            return null;
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "TripPlan probe failed");
            return null;
        }
        finally
        {
            _tfnswGate.Release();
        }
    }

    private async Task<IReadOnlyList<TfNswCoordinateStop>> SafeCoordinateRequestAsync(
        Point origin,
        int radiusMetres,
        CancellationToken ct)
    {
        await _tfnswGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            return await _tfnsw.CoordinateRequestAsync(origin, radiusMetres, ct).ConfigureAwait(false);
        }
        catch (NotImplementedException)
        {
            return Array.Empty<TfNswCoordinateStop>();
        }
        catch (Exception ex)
        {
            _logger.LogDebug(ex, "CoordinateRequest probe failed");
            return Array.Empty<TfNswCoordinateStop>();
        }
        finally
        {
            _tfnswGate.Release();
        }
    }

    private static bool IsNearDestination(Point candidate, Point destination)
    {
        // Crude haversine: degree distance × ~111 km/degree. Good enough at this proximity.
        var d = candidate.Distance(destination) * 111_000.0;
        return d < DestinationProximityMetres;
    }

    private static string GeoBucketKey(Point p) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{p.X:F4},{p.Y:F4}");

    private static bool IsRailLike(string mode) => (mode ?? string.Empty).ToLowerInvariant() switch
    {
        "train" or "rail" or "heavy_rail" or "metro" or "light_rail" or "tram" or "lightrail" or "ferry" or "wharf" => true,
        _ => false,
    };

    private static NodeKind ClassifyStopKind(string mode) => (mode ?? string.Empty).ToLowerInvariant() switch
    {
        "train" or "rail" or "heavy_rail" or "metro" => NodeKind.TrainStation,
        "light_rail" or "tram" or "lightrail" => NodeKind.LightRailStop,
        "ferry" or "wharf" => NodeKind.Wharf,
        _ => NodeKind.BusStop,
    };

    private sealed record MeetingHub(Point Location, string Name, NodeKind Kind, string? StopId);
}
