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

    [Fact]
    public void Timeline_slides_late_so_the_driver_arrives_just_before_the_target()
    {
        // DepartAt is set well before the window, so a forward-from-DepartAt timeline made the driver
        // arrive ~40 min early and idle. The DTO slides the whole route later: the driver leaves
        // just-in-time and lands ArrivalSlack (5 min) before the target. Departure, destination ETA,
        // and every stop ETA all move by the same shift so the itinerary stays coherent.
        var departAt = new DateTimeOffset(2026, 5, 28, 9, 0, 0, TimeSpan.Zero);
        var target = departAt.AddHours(1); // 10:00
        var trip = new Trip(Guid.NewGuid(), "T", new Destination("Dest", Pt(151.01, -33.78)),
            departAt,
            new ArrivalWindow(target, target),
            Guid.NewGuid(), DateTimeOffset.UtcNow);

        var stop = new Stop(Guid.NewGuid(), Guid.NewGuid(), 0, Pt(151.09, -33.81), Guid.Empty,
            departAt.AddMinutes(13), Array.Empty<Guid>());
        var route = new DriverRoute(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 17.0, 0, new[] { stop });
        var solution = new Solution(Guid.NewGuid(), Guid.NewGuid(), "OR-Tools", 1.0, new[] { 0.0 }, new[] { route });

        var r = solution.ToDto(trip).Routes[0];

        // shift = (10:00 − 5 min − 9:00) − 17 min = 55 − 17 = 38 min.
        r.DestinationArrival.Should().Be(target.AddMinutes(-5)); // 9:55, 5 min before the target.
        r.Departure.Should().Be(target.AddMinutes(-5 - 17));     // 9:38 = arrival − driving.
        r.Stops[0].EstimatedArrival.Should().Be(departAt.AddMinutes(13 + 38)); // 9:51, shifted with the rest.
    }

    [Fact]
    public void A_window_too_tight_to_reach_keeps_the_earliest_possible_timing()
    {
        // When the driver can't reach the target even leaving at DepartAt, the shift pins to zero:
        // depart at DepartAt and arrive as early as the drive allows, rather than inventing a
        // departure earlier than the trip's scheduled start.
        var departAt = new DateTimeOffset(2026, 5, 28, 9, 0, 0, TimeSpan.Zero);
        var target = departAt.AddMinutes(10); // only 10 min of slack for a 17-min drive
        var trip = new Trip(Guid.NewGuid(), "T", new Destination("Dest", Pt(151.01, -33.78)),
            departAt,
            new ArrivalWindow(target, target),
            Guid.NewGuid(), DateTimeOffset.UtcNow);

        var stop = new Stop(Guid.NewGuid(), Guid.NewGuid(), 0, Pt(151.09, -33.81), Guid.Empty,
            departAt.AddMinutes(13), Array.Empty<Guid>());
        var route = new DriverRoute(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 17.0, 0, new[] { stop });
        var solution = new Solution(Guid.NewGuid(), Guid.NewGuid(), "OR-Tools", 1.0, new[] { 0.0 }, new[] { route });

        var r = solution.ToDto(trip).Routes[0];

        r.Departure.Should().Be(departAt);                          // no slide — leave on schedule.
        r.DestinationArrival.Should().Be(departAt.AddMinutes(17));   // arrive as soon as the drive ends.
        r.Stops[0].EstimatedArrival.Should().Be(departAt.AddMinutes(13)); // unshifted.
    }

    [Fact]
    public void Departure_and_destination_arrival_are_null_without_a_trip_to_anchor_on()
    {
        var stop = new Stop(Guid.NewGuid(), Guid.NewGuid(), 0, Pt(151.09, -33.81), Guid.Empty,
            DateTimeOffset.UtcNow, Array.Empty<Guid>());
        var route = new DriverRoute(Guid.NewGuid(), Guid.NewGuid(), Guid.NewGuid(), 17.0, 0, new[] { stop });
        var solution = new Solution(Guid.NewGuid(), Guid.NewGuid(), "OR-Tools", 1.0, new[] { 0.0 }, new[] { route });

        var r = solution.ToDto().Routes[0];
        r.DestinationArrival.Should().BeNull();
        r.Departure.Should().BeNull();
    }

    private static void Attach(Trip trip, Participant p)
    {
        var field = typeof(Trip).GetField("_participants", BindingFlags.NonPublic | BindingFlags.Instance)!;
        ((List<Participant>)field.GetValue(trip)!).Add(p);
    }
}
