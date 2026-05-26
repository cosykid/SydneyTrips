using NetTopologySuite.Geometries;
using Trips.Core.Abstractions;
using Trips.Core.Domain;
using Trips.Optimisation.Postprocess;

namespace Trips.Optimisation.Tests;

public class PostprocessorTests
{
    [Fact]
    public async Task WithRoutesClient_StampsLegDurations()
    {
        var driverId = Guid.NewGuid();
        var stopLoc = new Point(151.2, -33.9) { SRID = 4326 };
        var stops = new[]
        {
            new Stop(
                id: Guid.NewGuid(),
                driverRouteId: Guid.Empty,
                orderIndex: 0,
                location: stopLoc,
                candidateNodeId: Guid.NewGuid(),
                estimatedArrival: DateTimeOffset.UtcNow,
                pickups: new[] { Guid.NewGuid() }),
        };
        var route = new DriverRoute(Guid.NewGuid(), Guid.Empty, driverId, travelMins: 30, orderIndex: 0, stops);
        var solution = new Solution(
            id: Guid.NewGuid(), optimisationRunId: Guid.NewGuid(), label: "test",
            objective: 100, objectiveTerms: new[] { 10.0, 0, 0, 0, 0 },
            routes: new[] { route });

        var fakeClient = new FakeRoutesClient(legMins: 17);
        var pp = new SolutionPostprocessor(fakeClient, Microsoft.Extensions.Logging.Abstractions.NullLogger<SolutionPostprocessor>.Instance);
        var origins = new Dictionary<Guid, Point> { [driverId] = new Point(151.0, -33.8) { SRID = 4326 } };
        var dest = new Point(151.3, -33.9) { SRID = 4326 };
        var departAt = new DateTimeOffset(2026, 5, 23, 8, 0, 0, TimeSpan.Zero);

        var refined = await pp.PostprocessAsync(solution, departAt, origins, dest, default);

        Assert.NotSame(solution, refined);
        var refinedRoute = refined.Routes.Single();
        Assert.Equal(17 * 2, refinedRoute.TravelMins);          // 2 legs * 17 mins each
        Assert.Equal(departAt.AddMinutes(17), refinedRoute.Stops[0].EstimatedArrival);
    }

    [Fact]
    public async Task NullRoutesClient_ReturnsSolutionUnchanged()
    {
        // Build a minimal solution by hand.
        var driverId = Guid.NewGuid();
        var stops = new[]
        {
            new Stop(
                id: Guid.NewGuid(),
                driverRouteId: Guid.Empty,
                orderIndex: 0,
                location: new Point(151.2, -33.9) { SRID = 4326 },
                candidateNodeId: Guid.NewGuid(),
                estimatedArrival: new DateTimeOffset(2026, 5, 23, 8, 30, 0, TimeSpan.Zero),
                pickups: new[] { Guid.NewGuid() }),
        };
        var route = new DriverRoute(Guid.NewGuid(), Guid.Empty, driverId, travelMins: 30, orderIndex: 0, stops);
        var solution = new Solution(
            id: Guid.NewGuid(), optimisationRunId: Guid.NewGuid(), label: "test",
            objective: 100, objectiveTerms: new[] { 10.0, 0, 0, 0, 0 },
            routes: new[] { route });

        var pp = new SolutionPostprocessor();   // no IGoogleRoutesClient
        var origins = new Dictionary<Guid, Point> { [driverId] = new Point(151.0, -33.8) { SRID = 4326 } };
        var dest = new Point(151.3, -33.9) { SRID = 4326 };

        var refined = await pp.PostprocessAsync(solution, DateTimeOffset.UtcNow, origins, dest, default);
        Assert.Same(solution, refined);
    }
}

/// <summary>
/// Minimal fake implementing <see cref="IGoogleRoutesClient"/> for the postprocessor tests. Every
/// leg returns the same duration; matrix endpoint is unused by the postprocessor.
/// </summary>
internal sealed class FakeRoutesClient : IGoogleRoutesClient
{
    private readonly double _legMins;

    public FakeRoutesClient(double legMins)
    {
        _legMins = legMins;
    }

    public Task<double[,]> ComputeRouteMatrixAsync(IReadOnlyList<Point> origins, IReadOnlyList<Point> destinations, bool trafficAware, CancellationToken ct)
        => throw new NotImplementedException("postprocessor doesn't call matrix");

    public Task<GoogleRoutesResult> ComputeRoutesAsync(Point origin, Point destination, IReadOnlyList<Point> waypoints, bool optimizeWaypointOrder, CancellationToken ct)
    {
        // Build N+1 legs for N waypoints: origin → wp0 → wp1 → … → dest
        var legs = new List<GoogleRouteLeg>();
        var prev = origin;
        for (var i = 0; i < waypoints.Count; i++)
        {
            legs.Add(new GoogleRouteLeg(prev, waypoints[i], _legMins, _legMins * 1000));
            prev = waypoints[i];
        }
        legs.Add(new GoogleRouteLeg(prev, destination, _legMins, _legMins * 1000));
        return Task.FromResult(new GoogleRoutesResult(legs, legs.Count * _legMins, legs.Count * _legMins * 1000, null));
    }
}
