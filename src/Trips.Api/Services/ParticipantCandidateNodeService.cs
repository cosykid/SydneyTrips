using NetTopologySuite.Geometries;
using Trips.Core.Abstractions;
using Trips.Core.Domain;

namespace Trips.Api.Services;

/// <summary>
/// Builds the candidate pickup-node set for a participant.
///
/// <para><b>Model.</b> Pickup hubs are transit interchanges where a driver could collect a
/// passenger en route to the trip's destination. We source them from two corridors:</para>
/// <list type="number">
///   <item>The passenger's <em>own</em> PT corridor: TfNSW plans <c>home → destination</c>
///         and every PT-leg endpoint along that plan is admitted. These cover the case where
///         the passenger PTs partway and a driver picks them up at the alighting point.</item>
///   <item>Each <em>driver's</em> PT corridor: TfNSW plans <c>driver-home → destination</c>;
///         the PT-leg endpoints on those plans are the natural interchanges the driver passes
///         through. For each such hub, we probe TfNSW for a PT plan from the passenger's home
///         to the hub — if reachable within the passenger's walking + a soft PT budget, the
///         hub joins their candidate set. This is the channel that puts e.g. Epping in a
///         Randwick passenger's options when the driver is coming from Hornsby.</item>
/// </list>
///
/// <para>Home is always present so the solver can still choose doorstep pickup when that wins.
/// Hubs within <see cref="DestinationProximityMetres"/> of the trip destination are dropped from
/// both channels — those collapse "carpool" into "passenger PTs all the way", which defeats the
/// point. The <see cref="MaxPtMins"/> soft cap keeps far-flung hubs out of the set.</para>
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

    /// <summary>Soft cap on PT minutes for the driver-corridor channel; without this, e.g. a
    /// Bondi passenger would see Hornsby as a "reachable" hub via two-hour PT.</summary>
    private const int MaxPtMins = 90;

    private readonly ITfNswClient _tfnsw;
    private readonly ILogger<ParticipantCandidateNodeService> _logger;

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

        // `seen` tracks hubs we've already admitted so the driver-corridor pass doesn't double-add
        // a hub that was already on the passenger's own corridor. Keyed by 4dp geo bucket (~11m).
        var seen = new Dictionary<string, byte>();

        // Channel 1: passenger's own corridor.
        var ownPlan = await SafeTripPlanAsync(participant.Home, trip.DestinationLocation, planAt, ct);
        AdmitHubsFromPlan(participant, ownPlan, trip.DestinationLocation, seen);

        // Drivers get no further channels — they aren't picked up.
        if (participant.HasCar) return;

        // Channel 2: hubs along the drivers' corridors (the key fix — these are the meeting
        // points the *driver* will naturally pass through, not just those the passenger would).
        var driverHubs = await CollectDriverCorridorHubsAsync(trip, planAt, ct);

        foreach (var hub in driverHubs)
        {
            var key = GeoBucketKey(hub.Location);
            if (seen.ContainsKey(key)) continue;

            // Probe: can this passenger actually PT to this hub in budget?
            var probe = await SafeTripPlanAsync(participant.Home, hub.Location, planAt, ct);
            if (probe is null) continue;
            if (probe.TotalWalkMins > participant.WalkBudgetMins) continue;
            if (probe.TotalPtMins <= 0) continue;          // must actually be a PT trip
            if (probe.TotalPtMins > MaxPtMins) continue;    // soft cap

            // For the driver-corridor channel the probe plan IS the home→hub journey, so its
            // legs in full form the path. Truncating to the leg that ends at the hub would clip
            // off any final walk that's actually part of reaching the meeting point.
            var probePath = BuildPathLineString(probe.Legs, prefixLength: probe.Legs.Count);

            participant.AddCandidateNode(new CandidateNode(
                id: Guid.NewGuid(),
                participantId: participant.Id,
                kind: hub.Kind,
                location: hub.Location,
                walkMins: probe.TotalWalkMins,
                ptMins: probe.TotalPtMins,
                displayName: hub.Name,
                path: probePath));
            seen[key] = 0;
        }
    }

    /// <summary>
    /// Walk a TfNSW plan's legs, accumulating walk + PT minutes, and admit each PT-leg endpoint
    /// as a candidate node (filtered by walk budget + destination proximity). The
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
            if (totalWalk > participant.WalkBudgetMins) continue;
            if (IsNearDestination(leg.To, tripDestination)) continue;

            var key = GeoBucketKey(leg.To);
            if (!seen.TryAdd(key, 0)) continue;

            var path = BuildPathLineString(plan.Legs, prefixLength: i + 1);

            participant.AddCandidateNode(new CandidateNode(
                id: Guid.NewGuid(),
                participantId: participant.Id,
                kind: ClassifyStopKind(leg.Mode),
                location: leg.To,
                walkMins: totalWalk,
                ptMins: totalPt,
                displayName: leg.ToName ?? $"{leg.Mode} stop",
                path: path));
        }
    }

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
    /// Plan each driver's own home→destination corridor in parallel and harvest unique PT-leg
    /// endpoints as candidate meeting hubs. These are the stations the driver naturally rolls
    /// through on the way to the venue.
    /// </summary>
    private async Task<IReadOnlyList<DriverCorridorHub>> CollectDriverCorridorHubsAsync(
        Trip trip,
        DateTimeOffset planAt,
        CancellationToken ct)
    {
        var drivers = trip.Participants.Where(p => p.HasCar).ToList();
        if (drivers.Count == 0) return Array.Empty<DriverCorridorHub>();

        var planTasks = drivers.Select(d => SafeTripPlanAsync(d.Home, trip.DestinationLocation, planAt, ct)).ToArray();
        var plans = await Task.WhenAll(planTasks).ConfigureAwait(false);

        var hubs = new Dictionary<string, DriverCorridorHub>();
        foreach (var plan in plans)
        {
            if (plan is null) continue;
            foreach (var leg in plan.Legs)
            {
                if (string.Equals(leg.Mode, "walk", StringComparison.OrdinalIgnoreCase)) continue;
                if (leg.To is null) continue;
                if (IsNearDestination(leg.To, trip.DestinationLocation)) continue;
                var key = GeoBucketKey(leg.To);
                if (hubs.ContainsKey(key)) continue;
                hubs[key] = new DriverCorridorHub(
                    Location: leg.To,
                    Name: leg.ToName ?? $"{leg.Mode} stop",
                    Kind: ClassifyStopKind(leg.Mode));
            }
        }
        return hubs.Values.ToList();
    }

    private async Task<TfNswTripPlan?> SafeTripPlanAsync(
        Point origin,
        Point destination,
        DateTimeOffset planAt,
        CancellationToken ct)
    {
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
    }

    private static bool IsNearDestination(Point candidate, Point destination)
    {
        // Crude haversine: degree distance × ~111 km/degree. Good enough at this proximity.
        var d = candidate.Distance(destination) * 111_000.0;
        return d < DestinationProximityMetres;
    }

    private static string GeoBucketKey(Point p) =>
        string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{p.X:F4},{p.Y:F4}");

    private static NodeKind ClassifyStopKind(string mode) => (mode ?? string.Empty).ToLowerInvariant() switch
    {
        "train" or "rail" or "heavy_rail" or "metro" => NodeKind.TrainStation,
        "light_rail" or "tram" or "lightrail" => NodeKind.LightRailStop,
        "ferry" or "wharf" => NodeKind.Wharf,
        _ => NodeKind.BusStop,
    };

    private sealed record DriverCorridorHub(Point Location, string Name, NodeKind Kind);
}
