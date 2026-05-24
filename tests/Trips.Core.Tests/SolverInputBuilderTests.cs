using System.Reflection;
using NetTopologySuite.Geometries;
using Trips.Core.Abstractions;
using Trips.Core.Domain;

namespace Trips.Core.Tests;

/// <summary>
/// Coverage for <see cref="SolverInputBuilder"/>'s candidate-node deduplication: per-participant
/// CandidateNode rows that refer to the same physical stop (TfNSW stop_id, or co-located by lat/lng)
/// must collapse to a single <see cref="SolverNode"/> so the solver doesn't see duplicate co-located
/// places.
/// </summary>
public class SolverInputBuilderTests
{
    [Fact]
    public void Two_passengers_sharing_external_id_produce_one_solver_node()
    {
        // Both passengers picked Chatswood Station (TfNSW stop_id = "200060") as a candidate. Their
        // CandidateNode rows are per-participant (distinct ids, possibly different walk/PT mins) but
        // the canonical key is the same — so we expect exactly one SolverNode for the station.
        var (trip, run, p1, p2) = BuildTrip();
        AddCandidate(p1, externalId: "200060", lon: 151.1830, lat: -33.7969, walk: 5, pt: 0);
        AddCandidate(p2, externalId: "200060", lon: 151.1830, lat: -33.7969, walk: 12, pt: 3);

        var input = SolverInputBuilder.Build(trip, run);

        // Layout: [driver-origin (0), candidate (1), destination (2)].
        Assert.Equal(3, input.Nodes.Count);
        var candidateNodes = input.Nodes.Where(n => n.CandidateNodeId is not null && n.CandidateNodeId != Guid.Empty).ToList();
        Assert.Single(candidateNodes);

        // Both passengers point at the same canonical index, but each carries their own walk/PT mins.
        Assert.Equal(2, input.Passengers.Count);
        Assert.Equal(input.Passengers[0].CandidateNodeIndices[0], input.Passengers[1].CandidateNodeIndices[0]);
        Assert.Equal(5, input.Passengers[0].WalkPtMinsByNodeIndex[0]);
        Assert.Equal(15, input.Passengers[1].WalkPtMinsByNodeIndex[0]); // walk+pt
    }

    [Fact]
    public void Co_located_candidates_without_external_id_dedup_via_geo_bucket()
    {
        // Two passengers happen to live at the same address — their Home candidates have no
        // ExternalId, but their lat/lng round into the same ~10m bucket and should still collapse.
        var (trip, run, p1, p2) = BuildTrip();
        // Both points round to (151.2093, -33.8688) at 4 decimal places — same ~10m bucket.
        AddCandidate(p1, externalId: null, lon: 151.20931, lat: -33.86881, walk: 0, pt: 0);
        AddCandidate(p2, externalId: null, lon: 151.20933, lat: -33.86883, walk: 0, pt: 0);

        var input = SolverInputBuilder.Build(trip, run);

        var candidateNodes = input.Nodes.Where(n => n.CandidateNodeId is not null && n.CandidateNodeId != Guid.Empty).ToList();
        Assert.Single(candidateNodes);
        Assert.Equal(input.Passengers[0].CandidateNodeIndices[0], input.Passengers[1].CandidateNodeIndices[0]);
    }

    [Fact]
    public void Distinct_external_ids_do_not_collapse()
    {
        // Different stops — must remain distinct SolverNodes even though they're geographically close.
        var (trip, run, p1, p2) = BuildTrip();
        AddCandidate(p1, externalId: "200060", lon: 151.1830, lat: -33.7969, walk: 5, pt: 0);
        AddCandidate(p2, externalId: "200061", lon: 151.1832, lat: -33.7971, walk: 5, pt: 0);

        var input = SolverInputBuilder.Build(trip, run);

        var candidateNodes = input.Nodes.Where(n => n.CandidateNodeId is not null && n.CandidateNodeId != Guid.Empty).ToList();
        Assert.Equal(2, candidateNodes.Count);
        Assert.NotEqual(input.Passengers[0].CandidateNodeIndices[0], input.Passengers[1].CandidateNodeIndices[0]);
    }

    [Fact]
    public void Passenger_index_list_dedups_within_itself()
    {
        // A passenger with two CN rows that share a canonical key (e.g. TfNSW returns duplicates)
        // should not get a duplicated index — otherwise the solver sees a phantom second pickup
        // option at the same place.
        var (trip, run, p1, _) = BuildTrip();
        AddCandidate(p1, externalId: "200060", lon: 151.1830, lat: -33.7969, walk: 5, pt: 0);
        AddCandidate(p1, externalId: "200060", lon: 151.1830, lat: -33.7969, walk: 9, pt: 0);

        var input = SolverInputBuilder.Build(trip, run);

        var passenger = input.Passengers.Single(p => p.ParticipantId == p1.Id);
        Assert.Single(passenger.CandidateNodeIndices);
        Assert.Single(passenger.CandidateNodeIndices.Distinct());
        // The first occurrence's walk/PT mins win — solver sees one consistent value.
        Assert.Equal(5, passenger.WalkPtMinsByNodeIndex[0]);
    }

    [Fact]
    public void CanonicalKey_prefers_external_id_over_geo_bucket()
    {
        var withExt = new CandidateNode(Guid.NewGuid(), Guid.NewGuid(), NodeKind.TrainStation,
            Pt(151.1830, -33.7969), walkMins: 5, ptMins: 0, externalId: "200060");
        var withoutExt = new CandidateNode(Guid.NewGuid(), Guid.NewGuid(), NodeKind.TrainStation,
            Pt(151.1830, -33.7969), walkMins: 5, ptMins: 0);

        Assert.StartsWith("ext:", SolverInputBuilder.CanonicalKey(withExt));
        Assert.StartsWith("geo:", SolverInputBuilder.CanonicalKey(withoutExt));
    }

    // ---------- helpers ----------

    private static (Trip trip, OptimisationRun run, Participant p1, Participant p2) BuildTrip()
    {
        var tripId = Guid.NewGuid();
        var trip = new Trip(
            id: tripId,
            name: "T",
            destination: new Destination("Dest", Pt(151.30, -33.50)),
            departAt: DateTimeOffset.UtcNow,
            arrivalWindow: new ArrivalWindow(DateTimeOffset.UtcNow, DateTimeOffset.UtcNow.AddHours(1)),
            ownerId: Guid.NewGuid(),
            createdAt: DateTimeOffset.UtcNow);

        var driver = new Participant(Guid.NewGuid(), tripId, "Driver", Pt(151.20, -33.86), hasCar: true, seats: 4,
            preferences: new Preferences(10, 10, 1.0));
        var p1 = new Participant(Guid.NewGuid(), tripId, "P1", Pt(151.21, -33.87), hasCar: false, seats: 0,
            preferences: new Preferences(10, 10, 1.0));
        var p2 = new Participant(Guid.NewGuid(), tripId, "P2", Pt(151.22, -33.88), hasCar: false, seats: 0,
            preferences: new Preferences(10, 10, 1.0));
        AttachParticipant(trip, driver);
        AttachParticipant(trip, p1);
        AttachParticipant(trip, p2);

        var run = new OptimisationRun(Guid.NewGuid(), tripId, ObjectiveWeights.Balanced,
            SolverKind.OrTools, DateTimeOffset.UtcNow);
        return (trip, run, p1, p2);
    }

    private static void AddCandidate(Participant p, string? externalId, double lon, double lat, int walk, int pt)
    {
        p.AddCandidateNode(new CandidateNode(
            id: Guid.NewGuid(),
            participantId: p.Id,
            kind: externalId is not null ? NodeKind.TrainStation : NodeKind.Home,
            location: Pt(lon, lat),
            walkMins: walk,
            ptMins: pt,
            externalId: externalId));
    }

    private static Point Pt(double lon, double lat) => new(lon, lat) { SRID = 4326 };

    /// <summary>
    /// Trip's participant collection is EF-populated (no public AddParticipant), so unit tests
    /// poke the private backing list directly to avoid spinning up a DbContext.
    /// </summary>
    private static void AttachParticipant(Trip trip, Participant participant)
    {
        var field = typeof(Trip).GetField("_participants", BindingFlags.NonPublic | BindingFlags.Instance);
        var list = (List<Participant>)field!.GetValue(trip)!;
        list.Add(participant);
    }
}
