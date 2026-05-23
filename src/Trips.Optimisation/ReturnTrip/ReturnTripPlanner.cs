using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using Trips.Core.Abstractions;
using Trips.Core.Domain;

namespace Trips.Optimisation.ReturnTrip;

/// <summary>
/// Plans return-leg trips from the original trip's destination back to each passenger's chosen
/// drop-off. Departures within a configurable window are clustered together; each cluster gets its
/// own independent DARP solve via <see cref="ISolver"/>.
/// </summary>
public interface IReturnTripPlanner
{
    /// <summary>
    /// Plan the return leg. Returns one <see cref="Solution"/> per departure-time cluster.
    /// </summary>
    Task<IReadOnlyList<Solution>> PlanReturnAsync(Guid tripId, IReadOnlyList<ReturnRequest> requests, CancellationToken ct);
}

/// <summary>One passenger's return-leg desire — when they want to leave and where they want to be dropped.</summary>
public sealed record ReturnRequest(Guid ParticipantId, DateTime DesiredDeparture, Point DesiredDropoff);

/// <summary>Tuning knobs for the planner. Exposed via DI so callers can tighten/loosen the window.</summary>
/// <param name="ClusterWindowMinutes">Half-width of the cluster window. Departures within ±this many
/// minutes of a cluster's anchor get bucketed together. Default 15.</param>
public sealed record ReturnTripPlannerOptions(int ClusterWindowMinutes = 15)
{
    public static ReturnTripPlannerOptions Default { get; } = new();
}

public sealed class ReturnTripPlanner : IReturnTripPlanner
{
    private readonly ISolver _solver;
    private readonly ITripRepository _trips;
    private readonly ReturnTripPlannerOptions _options;
    private readonly ILogger<ReturnTripPlanner> _logger;

    public ReturnTripPlanner(
        ISolver solver,
        ITripRepository trips,
        ReturnTripPlannerOptions? options = null,
        ILogger<ReturnTripPlanner>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(solver);
        ArgumentNullException.ThrowIfNull(trips);
        _solver = solver;
        _trips = trips;
        _options = options ?? ReturnTripPlannerOptions.Default;
        _logger = logger ?? NullLogger<ReturnTripPlanner>.Instance;
    }

    public async Task<IReadOnlyList<Solution>> PlanReturnAsync(
        Guid tripId,
        IReadOnlyList<ReturnRequest> requests,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(requests);
        var trip = await _trips.GetWithParticipantsAsync(tripId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Trip {tripId} not found.");

        if (requests.Count == 0)
        {
            return Array.Empty<Solution>();
        }

        var clusters = ClusterByDeparture(requests, _options.ClusterWindowMinutes);
        _logger.LogInformation("ReturnTripPlanner: trip {Trip} → {Count} clusters from {N} requests",
            tripId, clusters.Count, requests.Count);

        var solutions = new List<Solution>(clusters.Count);
        foreach (var cluster in clusters)
        {
            ct.ThrowIfCancellationRequested();
            var input = BuildReturnInput(trip, cluster);
            // No drivers (e.g. all passengers are non-drivers) → skip cluster. This shouldn't happen
            // for normal trips where the same drivers come back; we synthesise a single driver
            // (originating at the trip destination) when zero are available.
            var solution = await _solver.SolveAsync(input, ct).ConfigureAwait(false);
            solutions.Add(solution);
        }
        return solutions;
    }

    /// <summary>
    /// Bucket requests into clusters by departure time. Algorithm: sort by departure, walk the sorted
    /// list and start a new cluster whenever the next request is &gt; <paramref name="windowMinutes"/>
    /// after the *first* request in the current cluster. This produces clusters whose total span is
    /// bounded by <paramref name="windowMinutes"/>, which keeps each independent solve cohesive.
    /// </summary>
    public static IReadOnlyList<IReadOnlyList<ReturnRequest>> ClusterByDeparture(
        IReadOnlyList<ReturnRequest> requests, int windowMinutes)
    {
        if (requests.Count == 0) return Array.Empty<IReadOnlyList<ReturnRequest>>();
        var window = TimeSpan.FromMinutes(Math.Max(1, windowMinutes));
        var sorted = requests.OrderBy(r => r.DesiredDeparture).ToList();
        var clusters = new List<List<ReturnRequest>>();
        var current = new List<ReturnRequest> { sorted[0] };
        var anchor = sorted[0].DesiredDeparture;
        for (var i = 1; i < sorted.Count; i++)
        {
            var r = sorted[i];
            if (r.DesiredDeparture - anchor <= window)
            {
                current.Add(r);
            }
            else
            {
                clusters.Add(current);
                current = new List<ReturnRequest> { r };
                anchor = r.DesiredDeparture;
            }
        }
        clusters.Add(current);
        return clusters.Select(c => (IReadOnlyList<ReturnRequest>)c).ToList();
    }

    /// <summary>
    /// Build a <see cref="SolverInput"/> for one return-leg cluster. The origin is the trip's
    /// destination; each cluster member becomes a passenger node at their desired drop-off. Drivers
    /// are carried over from the original trip (their cars came with them). Travel times are a coarse
    /// haversine approximation — the post-processor or a future enhancement can snap to real Google
    /// Routes results.
    /// </summary>
    public static SolverInput BuildReturnInput(Trip trip, IReadOnlyList<ReturnRequest> cluster)
    {
        ArgumentNullException.ThrowIfNull(trip);
        ArgumentNullException.ThrowIfNull(cluster);

        var nodes = new List<SolverNode>();
        var drivers = new List<SolverDriver>();
        var passengers = new List<SolverPassenger>();

        // Node 0 = the original destination (origin of the return leg).
        nodes.Add(new SolverNode(Index: 0, Kind: NodeKind.TrainStation, CandidateNodeId: null));
        var originIndex = 0;

        // Drivers are the trip's car-owners; they start at the origin (i.e. trip destination).
        var driverParticipants = trip.Participants.Where(p => p.HasCar).ToList();
        foreach (var d in driverParticipants)
        {
            drivers.Add(new SolverDriver(d.Id, OriginNodeIndex: originIndex, Seats: d.Seats));
        }
        if (drivers.Count == 0 && cluster.Count > 0)
        {
            // No drivers in the original trip — synthesise a single one with capacity to carry the
            // whole cluster so the DARP is at least feasible. Real callers should provide drivers.
            drivers.Add(new SolverDriver(cluster[0].ParticipantId, OriginNodeIndex: originIndex,
                Seats: Math.Max(1, cluster.Count)));
        }

        // Each passenger gets one candidate node = their desired drop-off, plus a shared "destination"
        // sink. We pick the geometric centroid of all drop-offs as the final destination so the solver
        // has a single common endpoint. Each driver then drops off their passengers and ends at the
        // centroid; in practice this is the meeting point or simply the end of the leg.
        var passengerNodes = new List<int>(cluster.Count);
        var passengerPoints = new List<Point>(cluster.Count);
        for (var i = 0; i < cluster.Count; i++)
        {
            var idx = nodes.Count;
            nodes.Add(new SolverNode(idx, NodeKind.Home, CandidateNodeId: Guid.NewGuid()));
            passengerNodes.Add(idx);
            passengerPoints.Add(cluster[i].DesiredDropoff);
            passengers.Add(new SolverPassenger(
                ParticipantId: cluster[i].ParticipantId,
                CandidateNodeIndices: new[] { idx },
                WalkPtMinsByNodeIndex: new[] { 0 }));
        }
        // Destination = centroid of all drop-offs.
        var destIdx = nodes.Count;
        nodes.Add(new SolverNode(destIdx, NodeKind.TrainStation, CandidateNodeId: null));
        var centroid = ComputeCentroid(passengerPoints);

        // Build a haversine matrix. Indices: 0 = trip destination (origin), 1..N = passenger drops, last = centroid.
        var locations = new Point[nodes.Count];
        locations[0] = trip.DestinationLocation;
        for (var i = 0; i < passengerPoints.Count; i++)
        {
            locations[1 + i] = passengerPoints[i];
        }
        locations[destIdx] = centroid;

        var n = nodes.Count;
        var matrix = new double[n, n];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
            {
                matrix[i, j] = i == j ? 0.0 : EstimateDriveMinutes(locations[i], locations[j]);
            }
        }

        // Use Balanced weights for return-trip — passengers care most about getting home efficiently.
        var weights = ObjectiveWeights.Balanced;
        return new SolverInput(
            RunId: Guid.NewGuid(),
            TripId: trip.Id,
            Weights: weights,
            Drivers: drivers,
            Passengers: passengers,
            Nodes: nodes,
            TravelMatrix: matrix,
            DepartAt: cluster.Min(c => c.DesiredDeparture),
            WarmStartHint: null);
    }

    private static Point ComputeCentroid(IReadOnlyList<Point> points)
    {
        if (points.Count == 0) return new Point(0, 0) { SRID = 4326 };
        var lon = points.Average(p => p.X);
        var lat = points.Average(p => p.Y);
        return new Point(lon, lat) { SRID = 4326 };
    }

    /// <summary>Coarse Sydney-traffic-aware driving estimate: ~50 km/h average → ≈1.2 min/km.</summary>
    private static double EstimateDriveMinutes(Point a, Point b)
    {
        const double earthKm = 6371.0088;
        var lat1 = a.Y * Math.PI / 180.0;
        var lat2 = b.Y * Math.PI / 180.0;
        var dLat = (b.Y - a.Y) * Math.PI / 180.0;
        var dLon = (b.X - a.X) * Math.PI / 180.0;
        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2) + Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(h), Math.Sqrt(1 - h));
        var km = earthKm * c;
        // 1.3× road-curve factor × 1.2 min/km → ≈1.56 min/km on average for Sydney urban traffic.
        return km * 1.56;
    }
}
