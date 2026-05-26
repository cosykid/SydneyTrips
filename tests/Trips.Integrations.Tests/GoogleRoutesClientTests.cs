using FluentAssertions;
using NetTopologySuite.Geometries;

namespace Trips.Integrations.Tests;

[Collection(MockServerCollection.Name)]
public sealed class GoogleRoutesClientTests : MockServerTestBase
{
    private static readonly GeometryFactory Geom = new(new PrecisionModel(), 4326);

    public GoogleRoutesClientTests(MockServerFixture fixture) : base(fixture) { }

    [Fact]
    public async Task ComputeRouteMatrixAsync_fills_matrix_with_minutes()
    {
        var client = ClientFactory.GoogleRoutes(Fixture.Servers);
        var origins = new[]
        {
            Geom.CreatePoint(new Coordinate(151.2093, -33.8688)),
            Geom.CreatePoint(new Coordinate(151.2509, -33.8919)),
        };
        var destinations = new[]
        {
            Geom.CreatePoint(new Coordinate(151.2796, -33.8908)),
            Geom.CreatePoint(new Coordinate(151.2832, -33.8995)),
        };

        var matrix = await client.ComputeRouteMatrixAsync(origins, destinations, trafficAware: false, CancellationToken.None);

        matrix.GetLength(0).Should().Be(2);
        matrix.GetLength(1).Should().Be(2);
        // Fixture: 1320s ⇒ 22 minutes for (0,0).
        matrix[0, 0].Should().BeApproximately(22.0, 0.01);
        matrix[0, 1].Should().BeApproximately(27.0, 0.01);
        matrix[1, 0].Should().BeApproximately(23.0, 0.01);
    }

    [Fact]
    public async Task ComputeRoutesAsync_returns_legs_and_optimized_order()
    {
        var client = ClientFactory.GoogleRoutes(Fixture.Servers);

        var origin = Geom.CreatePoint(new Coordinate(151.2093, -33.8688));
        var destination = Geom.CreatePoint(new Coordinate(151.2093, -33.8688));
        var waypoints = new[]
        {
            Geom.CreatePoint(new Coordinate(151.2509, -33.8919)),
            Geom.CreatePoint(new Coordinate(151.2796, -33.8908)),
        };

        var result = await client.ComputeRoutesAsync(origin, destination, waypoints, optimizeWaypointOrder: true, CancellationToken.None);

        result.Legs.Should().HaveCount(3);
        result.TotalDurationMins.Should().BeApproximately(43.0, 0.01);
        result.OptimisedWaypointOrder.Should().Equal(1, 0);
    }

    [Fact]
    public void ParseDurationToMinutes_handles_seconds_string()
    {
        Trips.Integrations.Clients.GoogleRoutesClient
            .ParseDurationToMinutes("90s").Should().BeApproximately(1.5, 0.001);
        Trips.Integrations.Clients.GoogleRoutesClient
            .ParseDurationToMinutes("").Should().Be(0);
    }
}
