using System.Reflection;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using Trips.Api.Services;
using Trips.Core.Abstractions;
using Trips.Core.Domain;

namespace Trips.Api.Tests;

/// <summary>
/// Unit tests for <see cref="ParticipantCandidateNodeService"/> using a hand-fed
/// <see cref="ITfNswClient"/>. These pin the behaviours added to fix the "car detours to a
/// passenger's doorstep" problem: meeting hubs near a driver are offered to passengers, walking is
/// not a hard feasibility cap, and a passenger is never left with only Home when any stop is
/// reachable. No database / container — pure domain + fake client.
/// </summary>
public sealed class ParticipantCandidateNodeServiceTests
{
    private static readonly GeometryFactory Geom = new(new PrecisionModel(), 4326);
    private static Point Pt(double lng, double lat) => Geom.CreatePoint(new Coordinate(lng, lat)) ;

    [Fact]
    public async Task Admits_driver_proximity_hub_even_when_the_walk_exceeds_the_old_budget()
    {
        var dest = Pt(151.01, -33.78);
        var driverHome = Pt(151.10, -33.81);
        var passengerHome = Pt(151.20, -33.89);
        var nearHub = Pt(151.11, -33.80);     // reachable: walk 20 + pt 15 = 35 ≤ cap
        var farHub = Pt(151.12, -33.79);      // too far: walk 50 + pt 55 = 105 > cap

        var tfnsw = new FakeTfNswClient
        {
            CoordResponder = (origin, _) => Same(origin, driverHome)
                ? new[]
                {
                    Stop("near", "Near Hub", nearHub),
                    Stop("far", "Far Hub", farHub),
                }
                : Array.Empty<TfNswCoordinateStop>(),
        };
        // Probe plans the service will issue from the passenger's home.
        tfnsw.Plan((passengerHome, nearHub), EmptyPlan(walk: 20, pt: 15));
        tfnsw.Plan((passengerHome, farHub), EmptyPlan(walk: 50, pt: 55));

        var (trip, passenger) = BuildTrip(dest, driverHome, passengerHome, walkBudget: 12);
        var sut = new ParticipantCandidateNodeService(tfnsw, NullLogger<ParticipantCandidateNodeService>.Instance);

        await sut.PopulateAsync(passenger, trip, CancellationToken.None);

        var hubs = passenger.CandidateNodes.Where(n => n.Kind != NodeKind.Home).ToList();
        hubs.Should().ContainSingle("the reachable near-driver hub is admitted and the far one is over the access cap");
        var hub = hubs[0];
        hub.WalkMins.Should().Be(20, "a 20-min walk is admitted despite the 12-min walk budget — walking is no longer a hard cap");
        hub.Location.Coordinate.X.Should().BeApproximately(nearHub.X, 1e-9);
    }

    [Fact]
    public async Task Falls_back_to_a_nearby_stop_rather_than_leaving_the_passenger_with_only_Home()
    {
        var dest = Pt(151.01, -33.78);
        var driverHome = Pt(151.10, -33.81);
        var passengerHome = Pt(151.20, -33.89);
        var nearbyStop = Pt(151.205, -33.892);

        var tfnsw = new FakeTfNswClient
        {
            // No hubs near the driver, but the passenger has a walkable stop near home.
            CoordResponder = (origin, _) => Same(origin, passengerHome)
                ? new[] { Stop("local", "Local Stop", nearbyStop) }
                : Array.Empty<TfNswCoordinateStop>(),
        };
        tfnsw.Plan((passengerHome, nearbyStop), EmptyPlan(walk: 6, pt: 0)); // walk-only is fine

        var (trip, passenger) = BuildTrip(dest, driverHome, passengerHome, walkBudget: 12);
        var sut = new ParticipantCandidateNodeService(tfnsw, NullLogger<ParticipantCandidateNodeService>.Instance);

        await sut.PopulateAsync(passenger, trip, CancellationToken.None);

        passenger.CandidateNodes.Should().Contain(n => n.Kind == NodeKind.Home);
        passenger.CandidateNodes.Should().Contain(
            n => n.Kind != NodeKind.Home && Math.Abs(n.Location.Coordinate.X - nearbyStop.X) < 1e-9,
            "the fallback admits a reachable nearby stop so the passenger isn't reducible to a doorstep pickup");
    }

    // ---------- helpers ----------

    private static (Trip trip, Participant passenger) BuildTrip(Point dest, Point driverHome, Point passengerHome, int walkBudget)
    {
        var tripId = Guid.NewGuid();
        var trip = new Trip(tripId, "T", new Destination("Dest", dest), DateTimeOffset.UtcNow.AddHours(1),
            new ArrivalWindow(DateTimeOffset.UtcNow.AddHours(1), DateTimeOffset.UtcNow.AddHours(2)),
            Guid.NewGuid(), DateTimeOffset.UtcNow);

        var driver = new Participant(Guid.NewGuid(), tripId, "Driver", driverHome, hasCar: true, seats: 4,
            new Preferences(walkBudget, 10, 1.0));
        var passenger = new Participant(Guid.NewGuid(), tripId, "Passenger", passengerHome, hasCar: false, seats: 0,
            new Preferences(walkBudget, 10, 1.0));
        Attach(trip, driver);
        Attach(trip, passenger);
        return (trip, passenger);
    }

    private static void Attach(Trip trip, Participant p)
    {
        var field = typeof(Trip).GetField("_participants", BindingFlags.NonPublic | BindingFlags.Instance)!;
        ((List<Participant>)field.GetValue(trip)!).Add(p);
    }

    private static bool Same(Point a, Point b) => Math.Abs(a.X - b.X) < 1e-9 && Math.Abs(a.Y - b.Y) < 1e-9;

    private static TfNswCoordinateStop Stop(string id, string name, Point loc) =>
        new(id, name, loc, DistanceMeters: 500, Mode: "train");

    private static TfNswTripPlan EmptyPlan(int walk, int pt) =>
        new(Array.Empty<TfNswJourneyLeg>(), TotalWalkMins: walk, TotalPtMins: pt);

    private sealed class FakeTfNswClient : ITfNswClient
    {
        private readonly Dictionary<string, TfNswTripPlan> _plans = new();
        public Func<Point, int, IReadOnlyList<TfNswCoordinateStop>> CoordResponder { get; set; } = (_, _) => Array.Empty<TfNswCoordinateStop>();

        public void Plan((Point origin, Point dest) key, TfNswTripPlan plan) => _plans[Key(key.origin, key.dest)] = plan;

        public Task<TfNswTripPlan> TripPlanAsync(Point origin, Point destination, DateTimeOffset departAt, CancellationToken ct)
            => _plans.TryGetValue(Key(origin, destination), out var p)
                ? Task.FromResult(p)
                : throw new InvalidOperationException("no plan configured"); // service treats this as unreachable

        public Task<IReadOnlyList<TfNswCoordinateStop>> CoordinateRequestAsync(Point origin, int radiusMeters, CancellationToken ct)
            => Task.FromResult(CoordResponder(origin, radiusMeters));

        public Task<IReadOnlyList<TfNswDeparture>> DepartureAsync(string stopId, DateTimeOffset from, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TfNswDeparture>>(Array.Empty<TfNswDeparture>());

        public async IAsyncEnumerable<TfNswGtfsTripUpdate> GtfsRtTripUpdatesAsync(string mode, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.Yield();
            yield break;
        }

        private static string Key(Point o, Point d) =>
            string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{o.X:F3},{o.Y:F3}->{d.X:F3},{d.Y:F3}");
    }
}
