using NetTopologySuite.Geometries;
using Trips.Core.Abstractions;
using Trips.Core.Domain;
using Trips.Optimisation.Postprocess;

namespace Trips.Optimisation.Tests;

public class PostprocessorTests
{
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
