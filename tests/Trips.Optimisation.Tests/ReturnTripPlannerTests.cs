using FluentAssertions;
using NetTopologySuite.Geometries;
using Trips.Optimisation.ReturnTrip;

namespace Trips.Optimisation.Tests;

public sealed class ReturnTripPlannerTests
{
    [Fact]
    public void Six_participants_in_three_departure_windows_produce_three_clusters()
    {
        // Three buckets at 17:00, 18:00, 19:00 — each has two members within ±15 minutes.
        var now = new DateTime(2026, 5, 23, 0, 0, 0, DateTimeKind.Utc);
        var requests = new[]
        {
            new ReturnRequest(Guid.NewGuid(), now.AddHours(17), Pt(151.20, -33.86)),
            new ReturnRequest(Guid.NewGuid(), now.AddHours(17).AddMinutes(10), Pt(151.21, -33.87)),
            new ReturnRequest(Guid.NewGuid(), now.AddHours(18).AddMinutes(2), Pt(151.22, -33.88)),
            new ReturnRequest(Guid.NewGuid(), now.AddHours(18).AddMinutes(12), Pt(151.23, -33.89)),
            new ReturnRequest(Guid.NewGuid(), now.AddHours(19), Pt(151.24, -33.90)),
            new ReturnRequest(Guid.NewGuid(), now.AddHours(19).AddMinutes(8), Pt(151.25, -33.91)),
        };

        var clusters = ReturnTripPlanner.ClusterByDeparture(requests, windowMinutes: 15);
        clusters.Should().HaveCount(3);
        clusters[0].Should().HaveCount(2);
        clusters[1].Should().HaveCount(2);
        clusters[2].Should().HaveCount(2);
    }

    [Fact]
    public void Cluster_membership_uses_sorted_anchor()
    {
        // Insert deliberately unsorted. The expected output: a 17:00 cluster of three (17:00, 17:05,
        // 17:14), then a 17:30 cluster (just 17:30 alone — 30 > 15min from 17:00 anchor).
        var now = new DateTime(2026, 5, 23, 17, 0, 0, DateTimeKind.Utc);
        var requests = new[]
        {
            new ReturnRequest(Guid.NewGuid(), now.AddMinutes(14), Pt(0, 0)),
            new ReturnRequest(Guid.NewGuid(), now.AddMinutes(30), Pt(0, 0)),
            new ReturnRequest(Guid.NewGuid(), now, Pt(0, 0)),
            new ReturnRequest(Guid.NewGuid(), now.AddMinutes(5), Pt(0, 0)),
        };
        var clusters = ReturnTripPlanner.ClusterByDeparture(requests, windowMinutes: 15);
        clusters.Should().HaveCount(2);
        clusters[0].Should().HaveCount(3);
        clusters[1].Should().HaveCount(1);
    }

    [Fact]
    public void Empty_requests_produce_empty_clusters()
    {
        ReturnTripPlanner.ClusterByDeparture(Array.Empty<ReturnRequest>(), 15).Should().BeEmpty();
    }

    [Fact]
    public void Single_request_produces_single_cluster()
    {
        var r = new[] { new ReturnRequest(Guid.NewGuid(), DateTime.UtcNow, Pt(0, 0)) };
        var c = ReturnTripPlanner.ClusterByDeparture(r, 15);
        c.Should().HaveCount(1);
        c[0].Should().HaveCount(1);
    }

    [Fact]
    public void BuildReturnInput_places_drivers_at_trip_destination()
    {
        var tripId = Guid.NewGuid();
        var ownerId = Guid.NewGuid();
        var dest = Pt(151.30, -33.50);
        var trip = new Core.Domain.Trip(
            id: tripId,
            name: "T",
            destination: new Core.Domain.Destination("D", dest),
            departAt: DateTimeOffset.UtcNow,
            arrivalWindow: new Core.Domain.ArrivalWindow(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1)),
            ownerId: ownerId,
            createdAt: DateTimeOffset.UtcNow);
        // No participants — should still build a feasible input by synthesising one driver.
        var cluster = new[]
        {
            new ReturnRequest(Guid.NewGuid(), DateTime.UtcNow, Pt(151.20, -33.87)),
        };
        var input = ReturnTripPlanner.BuildReturnInput(trip, cluster);
        input.Drivers.Should().NotBeEmpty();
        input.Drivers[0].OriginNodeIndex.Should().Be(0);
        input.Nodes[0].CandidateNodeId.Should().BeNull(); // origin row
        input.Passengers.Should().HaveCount(1);
    }

    private static Point Pt(double lon, double lat) => new(lon, lat) { SRID = 4326 };
}
