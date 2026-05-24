using System.Reflection;
using FluentAssertions;
using NetTopologySuite.Geometries;
using Trips.Api.Mapping;
using Trips.Core.Domain;

namespace Trips.Api.Tests;

/// <summary>
/// Pins that <see cref="Mappers.ToDto(Solution, Trip?)"/> carries <em>each</em> passenger's own
/// home→pickup geometry, even when several passengers share one physical pickup hub. The solver
/// dedups co-located candidate nodes to a single canonical SolverNode, so a stop stores only one
/// <c>CandidateNodeId</c>; resolving the path by that id alone would give every passenger at the
/// hub the canonical passenger's route (or a straight line). This is the bug behind "some PT legs
/// render straight." The fix resolves walk/PT/path per (participant, canonical-key).
/// </summary>
public sealed class SolutionPickupPathMappingTests
{
    private static readonly GeometryFactory Geom = new(new PrecisionModel(), 4326);
    private static Point Pt(double lng, double lat) => Geom.CreatePoint(new Coordinate(lng, lat));

    [Fact]
    public void Each_passenger_at_a_shared_hub_keeps_its_own_path()
    {
        var tripId = Guid.NewGuid();
        var hub = Pt(151.09, -33.81);

        var trip = new Trip(tripId, "T", new Destination("Dest", Pt(151.01, -33.78)),
            DateTimeOffset.UtcNow.AddHours(1),
            new ArrivalWindow(DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddHours(2)),
            Guid.NewGuid(), DateTimeOffset.UtcNow);

        var a = new Participant(Guid.NewGuid(), tripId, "A", Pt(151.20, -33.89), false, 0, new Preferences(15, 10, 1.0));
        var b = new Participant(Guid.NewGuid(), tripId, "B", Pt(151.22, -33.92), false, 0, new Preferences(15, 10, 1.0));

        // Both reach the SAME hub (same ExternalId → same canonical key) but along their own,
        // distinctly-shaped paths (2 points vs 3 points).
        var pathA = Geom.CreateLineString(new[] { new Coordinate(151.20, -33.89), new Coordinate(151.09, -33.81) });
        var pathB = Geom.CreateLineString(new[] { new Coordinate(151.22, -33.92), new Coordinate(151.15, -33.86), new Coordinate(151.09, -33.81) });

        var cnA = new CandidateNode(Guid.NewGuid(), a.Id, NodeKind.TrainStation, hub, 12, 22, externalId: "hub-1", displayName: "Hub", path: pathA);
        var cnB = new CandidateNode(Guid.NewGuid(), b.Id, NodeKind.TrainStation, hub, 11, 39, externalId: "hub-1", displayName: "Hub", path: pathB);
        a.AddCandidateNode(cnA);
        b.AddCandidateNode(cnB);
        Attach(trip, a);
        Attach(trip, b);

        // One stop at the hub, picking up both — its CandidateNodeId is A's node (the canonical one).
        var stop = new Stop(Guid.NewGuid(), Guid.NewGuid(), 0, hub, cnA.Id, DateTimeOffset.UtcNow, new[] { a.Id, b.Id });
        var route = new DriverRoute(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 16.0, 0, new[] { stop });
        var solution = new Solution(Guid.NewGuid(), Guid.NewGuid(), "OR-Tools", 1.0, new[] { 0.0 }, new[] { route });

        var dto = solution.ToDto(trip);

        var pickups = dto.Routes[0].Stops[0].Pickups;
        var legA = pickups.Single(p => p.ParticipantId == a.Id);
        var legB = pickups.Single(p => p.ParticipantId == b.Id);

        legA.Path.Should().NotBeNull();
        legB.Path.Should().NotBeNull();
        // Each leg carries ITS OWN geometry (2 vs 3 points) — not both inheriting A's canonical path.
        legA.Path!.Coordinates.Should().HaveCount(2);
        legB.Path!.Coordinates.Should().HaveCount(3);
        // And the walk/PT split stays per-passenger too.
        legB.WalkMins.Should().Be(11);
        legB.PtMins.Should().Be(39);
    }

    private static void Attach(Trip trip, Participant p)
    {
        var field = typeof(Trip).GetField("_participants", BindingFlags.NonPublic | BindingFlags.Instance)!;
        ((List<Participant>)field.GetValue(trip)!).Add(p);
    }
}
