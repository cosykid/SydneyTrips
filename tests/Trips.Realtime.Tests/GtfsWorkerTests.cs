using System.Runtime.CompilerServices;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using NetTopologySuite.Geometries;
using Trips.Core.Abstractions;
using Trips.Core.Domain;
using Trips.Realtime.Gtfs;
using Trips.Realtime.Hubs;

namespace Trips.Realtime.Tests;

public sealed class GtfsWorkerTests
{
    private static readonly GeometryFactory Factory = new(new PrecisionModel(), 4326);

    [Fact]
    public async Task PollOnce_broadcasts_EtaUpdated_for_each_matched_participant()
    {
        var tripId = Guid.NewGuid();
        var participantId = Guid.NewGuid();
        var stopExternalId = "stop-200060";

        var (hub, broadcasts) = BuildHub();
        var registry = new InMemoryActiveTripRegistry();
        registry.OnConnectionJoined(tripId, "conn-1");

        var tfNsw = new FakeTfNswClient(new[]
        {
            new TfNswGtfsTripUpdate(
                TripId: "trip-X",
                VehicleId: "veh-1",
                Timestamp: DateTimeOffset.UtcNow,
                StopTimeUpdates: new[]
                {
                    new TfNswStopTimeUpdate(stopExternalId, DateTimeOffset.UtcNow.AddMinutes(8), null),
                }),
        });

        var trip = new Trip(
            id: tripId,
            name: "Trip",
            destination: new Destination("Dest", Factory.CreatePoint(new Coordinate(151.3, -33.6))),
            departAt: DateTimeOffset.UtcNow.AddMinutes(15),
            arrivalWindow: new ArrivalWindow(DateTimeOffset.UtcNow.AddMinutes(30), DateTimeOffset.UtcNow.AddHours(1)),
            ownerId: Guid.NewGuid(),
            createdAt: DateTimeOffset.UtcNow);
        var participant = new Participant(
            id: participantId,
            userId: Guid.NewGuid(),
            tripId: tripId,
            displayName: "P",
            home: Factory.CreatePoint(new Coordinate(151.2, -33.86)),
            hasCar: false,
            seats: 0,
            preferences: Preferences.Default);
        participant.AddCandidateNode(new CandidateNode(
            id: Guid.NewGuid(),
            participantId: participantId,
            kind: NodeKind.TrainStation,
            location: Factory.CreatePoint(new Coordinate(151.205, -33.865)),
            walkMins: 10,
            ptMins: 5,
            externalId: stopExternalId,
            displayName: "Test Station"));
        // Use reflection-friendly access — Trip._participants is private; we can't insert without
        // EF. Instead simulate the repository directly.
        var tripRepo = new Mock<ITripRepository>();
        tripRepo.Setup(r => r.GetWithParticipantsAsync(tripId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(MakeTripWith(trip, participant));

        var participantRepo = new Mock<IParticipantRepository>();
        participantRepo.Setup(r => r.GetWithCandidateNodesAsync(participantId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(participant);
        participantRepo.Setup(r => r.ListForTripAsync(tripId, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new[] { participant });

        var sp = new ServiceCollection();
        sp.AddSingleton<ITfNswClient>(tfNsw);
        sp.AddSingleton(tripRepo.Object);
        sp.AddSingleton(participantRepo.Object);
        sp.AddSingleton<ITripEventRepository>(Mock.Of<ITripEventRepository>());
        sp.AddSingleton<IClock>(new TestClock());
        sp.AddSingleton(hub);
        sp.AddSingleton<IActiveTripRegistry>(registry);

        var provider = sp.BuildServiceProvider();
        var scopeFactory = provider.GetRequiredService<IServiceScopeFactory>();
        var lifetime = new TestHostLifetime();

        var worker = new GtfsRealtimeWorker(scopeFactory, lifetime,
            NullLogger<GtfsRealtimeWorker>.Instance,
            Options.Create(new GtfsRealtimeOptions { Enabled = true, PollIntervalSeconds = 60 }));

        await worker.PollOnceForTestAsync(CancellationToken.None);

        broadcasts.Should().ContainSingle();
        broadcasts[0].PassengerId.Should().Be(participantId);
    }

    private static (IHubContext<TripHub, ITripHubClient> Hub, List<(Guid PassengerId, DateTime Eta)> Broadcasts) BuildHub()
    {
        var broadcasts = new List<(Guid PassengerId, DateTime Eta)>();

        var hub = new Mock<IHubContext<TripHub, ITripHubClient>>();
        var clients = new Mock<IHubClients<ITripHubClient>>();
        var client = new Mock<ITripHubClient>();

        client.Setup(c => c.EtaUpdated(It.IsAny<Guid>(), It.IsAny<DateTime>()))
            .Callback((Guid p, DateTime eta) => broadcasts.Add((p, eta)))
            .Returns(Task.CompletedTask);
        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(client.Object);
        hub.SetupGet(h => h.Clients).Returns(clients.Object);

        return (hub.Object, broadcasts);
    }

    private static Trip MakeTripWith(Trip trip, Participant participant)
    {
        // The Trip aggregate guards its _participants list internally. Use reflection to attach.
        var field = typeof(Trip).GetField("_participants", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!;
        var list = (List<Participant>)field.GetValue(trip)!;
        list.Add(participant);
        return trip;
    }

    private sealed class TestClock : IClock
    {
        public DateTimeOffset UtcNow { get; } = DateTimeOffset.UtcNow;
    }

    private sealed class TestHostLifetime : IHostApplicationLifetime
    {
        public CancellationToken ApplicationStarted => CancellationToken.None;
        public CancellationToken ApplicationStopping => CancellationToken.None;
        public CancellationToken ApplicationStopped => CancellationToken.None;
        public void StopApplication() { }
    }

    private sealed class FakeTfNswClient : ITfNswClient
    {
        private readonly IReadOnlyList<TfNswGtfsTripUpdate> _updates;
        public FakeTfNswClient(IReadOnlyList<TfNswGtfsTripUpdate> updates) { _updates = updates; }

        public Task<TfNswTripPlan> TripPlanAsync(Point origin, Point destination, DateTimeOffset departAt, CancellationToken ct) =>
            Task.FromResult(new TfNswTripPlan(Array.Empty<TfNswJourneyLeg>(), 0, 0));

        public Task<IReadOnlyList<TfNswCoordinateStop>> CoordinateRequestAsync(Point origin, int radiusMeters, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TfNswCoordinateStop>>(Array.Empty<TfNswCoordinateStop>());

        public Task<IReadOnlyList<TfNswDeparture>> DepartureAsync(string stopId, DateTimeOffset from, CancellationToken ct) =>
            Task.FromResult<IReadOnlyList<TfNswDeparture>>(Array.Empty<TfNswDeparture>());

        public async IAsyncEnumerable<TfNswGtfsTripUpdate> GtfsRtTripUpdatesAsync(string mode, [EnumeratorCancellation] CancellationToken ct)
        {
            foreach (var u in _updates)
            {
                await Task.Yield();
                yield return u;
            }
        }
    }
}
