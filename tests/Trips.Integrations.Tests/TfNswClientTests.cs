using FluentAssertions;
using NetTopologySuite.Geometries;

namespace Trips.Integrations.Tests;

[Collection(MockServerCollection.Name)]
public sealed class TfNswClientTests : MockServerTestBase
{
    private static readonly GeometryFactory Geom = new(new PrecisionModel(), 4326);

    public TfNswClientTests(MockServerFixture fixture) : base(fixture) { }

    [Fact]
    public async Task TripPlanAsync_returns_legs_for_cbd_to_bondi()
    {
        var client = ClientFactory.TfNsw(Fixture.Servers);

        var origin = Geom.CreatePoint(new Coordinate(151.2073, -33.8730));
        var destination = Geom.CreatePoint(new Coordinate(151.2796, -33.8908));
        var plan = await client.TripPlanAsync(origin, destination, new DateTimeOffset(2025, 1, 15, 8, 0, 0, TimeSpan.Zero), CancellationToken.None);

        plan.Legs.Should().HaveCount(3);
        plan.Legs[0].Mode.Should().Be("train");
        plan.Legs[0].DurationMins.Should().Be(8);
        plan.Legs[1].Mode.Should().Be("bus");
        plan.Legs[2].Mode.Should().Be("walk");
        plan.TotalWalkMins.Should().Be(4);
        plan.TotalPtMins.Should().Be(20);
    }

    [Fact]
    public async Task TripPlanAsync_routes_parramatta_to_manly_fixture()
    {
        var client = ClientFactory.TfNsw(Fixture.Servers);
        var origin = Geom.CreatePoint(new Coordinate(151.0021, -33.8175));
        var destination = Geom.CreatePoint(new Coordinate(151.2861, -33.8002));
        var plan = await client.TripPlanAsync(origin, destination, DateTimeOffset.UtcNow, CancellationToken.None);

        plan.Legs.Should().HaveCount(3);
        plan.Legs[0].Mode.Should().Be("train");
        plan.Legs[2].Mode.Should().Be("ferry");
    }

    [Fact]
    public async Task CoordinateRequestAsync_returns_stops_ordered_by_distance()
    {
        var client = ClientFactory.TfNsw(Fixture.Servers);
        var origin = Geom.CreatePoint(new Coordinate(151.207, -33.873));

        var stops = await client.CoordinateRequestAsync(origin, 800, CancellationToken.None);

        stops.Should().NotBeEmpty();
        stops.Should().BeInAscendingOrder(s => s.DistanceMeters);
        stops.First().StopId.Should().Be("200060");
        stops.First().Name.Should().Be("Town Hall Station");
    }

    [Fact]
    public async Task DepartureAsync_parses_realtime_offset_for_each_event()
    {
        var client = ClientFactory.TfNsw(Fixture.Servers);

        var departures = await client.DepartureAsync(
            "200060",
            new DateTimeOffset(2025, 1, 15, 8, 0, 0, TimeSpan.Zero),
            CancellationToken.None);

        departures.Should().HaveCount(3);
        departures[0].Line.Should().Be("T4");
        departures[0].EstimatedDeparture.Should().BeAfter(departures[0].PlannedDeparture);
        departures[0].VehicleId.Should().Be("T4-873-2025-01-15");
    }

    [Fact]
    public async Task GtfsRtTripUpdatesAsync_streams_trip_updates_from_protobuf()
    {
        var client = ClientFactory.TfNsw(Fixture.Servers);

        var updates = new List<Trips.Core.Abstractions.TfNswGtfsTripUpdate>();
        await foreach (var u in client.GtfsRtTripUpdatesAsync("trains", CancellationToken.None))
        {
            updates.Add(u);
        }

        updates.Should().HaveCount(2);
        updates[0].TripId.Should().Be("T4-873-2025-01-15");
        updates[0].VehicleId.Should().Be("TRAIN-873");
        updates[0].StopTimeUpdates.Should().HaveCount(2);
    }

    [Fact]
    public async Task GtfsRtTripUpdatesAsync_throws_for_unknown_mode()
    {
        var client = ClientFactory.TfNsw(Fixture.Servers);

        Func<Task> act = async () =>
        {
            await foreach (var _ in client.GtfsRtTripUpdatesAsync("aliens", CancellationToken.None))
            {
                // empty
            }
        };
        await act.Should().ThrowAsync<ArgumentException>().WithMessage("*aliens*");
    }
}
