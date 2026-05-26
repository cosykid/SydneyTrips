using NetTopologySuite.Geometries;
using Trips.Core.Abstractions;

namespace Trips.Api.Stubs;

/// <summary>
/// Stand-in <see cref="IGoogleRoutesClient"/> when WS2 isn't merged in.
/// Returns a Euclidean approximation so the optimisation path can run end-to-end in dev/tests.
/// </summary>
internal sealed class StubGoogleRoutesClient : IGoogleRoutesClient
{
    public Task<double[,]> ComputeRouteMatrixAsync(IReadOnlyList<Point> origins, IReadOnlyList<Point> destinations, bool trafficAware, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(origins);
        ArgumentNullException.ThrowIfNull(destinations);

        var matrix = new double[origins.Count, destinations.Count];
        for (var i = 0; i < origins.Count; i++)
        {
            for (var j = 0; j < destinations.Count; j++)
            {
                // 1 degree ≈ 111 km; assume 40 km/h average — gives min-scaled values
                // that look plausible enough for the solver in dev.
                var distMeters = origins[i].Distance(destinations[j]) * 111_000.0;
                matrix[i, j] = (distMeters / 1000.0) * (60.0 / 40.0);
            }
        }
        return Task.FromResult(matrix);
    }

    public Task<GoogleRoutesResult> ComputeRoutesAsync(Point origin, Point destination, IReadOnlyList<Point> waypoints, bool optimizeWaypointOrder, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(waypoints);

        var legs = new List<GoogleRouteLeg>();
        var prev = origin;
        foreach (var wp in waypoints)
        {
            legs.Add(new GoogleRouteLeg(prev, wp, DurationMins: 5, DistanceMeters: 1000));
            prev = wp;
        }
        legs.Add(new GoogleRouteLeg(prev, destination, DurationMins: 5, DistanceMeters: 1000));

        return Task.FromResult(new GoogleRoutesResult(
            Legs: legs,
            TotalDurationMins: legs.Count * 5,
            TotalDistanceMeters: legs.Count * 1000,
            OptimisedWaypointOrder: null));
    }
}
