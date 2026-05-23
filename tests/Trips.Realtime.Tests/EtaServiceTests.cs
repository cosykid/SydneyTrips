using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging.Abstractions;
using Moq;
using NetTopologySuite.Geometries;
using Trips.Core.Abstractions;
using Trips.Core.Domain;
using Trips.Realtime.Eta;
using Trips.Realtime.Hubs;

namespace Trips.Realtime.Tests;

public sealed class EtaServiceTests
{
    private static readonly GeometryFactory Factory = new(new PrecisionModel(), 4326);

    [Fact]
    public async Task RecomputeAndBroadcast_skips_when_trip_has_no_locked_solution()
    {
        var trip = MakeTrip(lockedSolution: null);
        var (mockHub, _) = MockHub();
        var service = BuildService(trip, run: null, mockHub.Object);

        await service.RecomputeAndBroadcastAsync(trip.Id, driverId: Guid.NewGuid(), driverLat: -33.86, driverLng: 151.21, CancellationToken.None);

        mockHub.Verify(h => h.Clients, Times.Never);
    }

    [Fact]
    public async Task RecomputeAndBroadcast_broadcasts_EtaUpdated_for_each_passenger_on_driver_route()
    {
        // One driver + two passengers on the same single-stop route.
        var driverId = Guid.NewGuid();
        var passenger1 = Guid.NewGuid();
        var passenger2 = Guid.NewGuid();

        var stop = new Stop(
            id: Guid.NewGuid(),
            driverRouteId: Guid.NewGuid(),
            orderIndex: 0,
            location: Factory.CreatePoint(new Coordinate(151.21, -33.86)),
            candidateNodeId: Guid.NewGuid(),
            estimatedArrival: DateTimeOffset.UtcNow.AddMinutes(20),
            pickups: new[] { passenger1, passenger2 });
        var route = new DriverRoute(
            id: Guid.NewGuid(),
            solutionId: Guid.NewGuid(),
            driverId: driverId,
            travelMins: 20,
            orderIndex: 0,
            stops: new[] { stop });
        var solution = new Solution(
            id: Guid.NewGuid(),
            optimisationRunId: Guid.NewGuid(),
            label: "test",
            objective: 1.0,
            objectiveTerms: new[] { 1.0 },
            routes: new[] { route });

        var trip = MakeTrip(lockedSolution: solution.Id);
        var run = new OptimisationRun(
            id: solution.OptimisationRunId,
            tripId: trip.Id,
            weights: ObjectiveWeights.Balanced,
            solver: SolverKind.Heuristic,
            startedAt: DateTimeOffset.UtcNow);
        run.MarkCompleted(solution, paretoAlternatives: null,
            new OptimisationStats(WallClock: TimeSpan.FromSeconds(1), IterationsOrNodes: 1, BestObjective: 1.0, LpRelaxation: null, Solver: SolverKind.Heuristic),
            DateTimeOffset.UtcNow);

        var (mockHub, clientProxy) = MockHub();
        var receivedEtas = new List<(Guid Passenger, DateTime Eta)>();
        clientProxy
            .Setup(c => c.SendCoreAsync("EtaUpdated", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback((string _, object?[] args, CancellationToken _) =>
            {
                var p = (Guid)args[0]!;
                var eta = (DateTime)args[1]!;
                receivedEtas.Add((p, eta));
            })
            .Returns(Task.CompletedTask);

        var service = BuildService(trip, run, mockHub.Object);
        await service.RecomputeAndBroadcastAsync(trip.Id, driverId, driverLat: -33.85, driverLng: 151.20, CancellationToken.None);

        receivedEtas.Should().HaveCount(2);
        receivedEtas.Select(t => t.Passenger).Should().Contain(new[] { passenger1, passenger2 });
    }

    private static (Mock<IHubContext<TripHub, ITripHubClient>> Hub, Mock<IClientProxy> ClientProxy) MockHub()
    {
        var hub = new Mock<IHubContext<TripHub, ITripHubClient>>();
        var clients = new Mock<IHubClients<ITripHubClient>>();
        var clientProxy = new Mock<IClientProxy>();
        var typedProxy = new HubClientProxy(clientProxy.Object);

        clients.Setup(c => c.Group(It.IsAny<string>())).Returns(typedProxy);
        hub.SetupGet(h => h.Clients).Returns(clients.Object);
        return (hub, clientProxy);
    }

    private static Trip MakeTrip(Guid? lockedSolution)
    {
        var trip = new Trip(
            id: Guid.NewGuid(),
            name: "Trip",
            destination: new Destination("Palm Beach", Factory.CreatePoint(new Coordinate(151.3247, -33.5984))),
            departAt: DateTimeOffset.UtcNow.AddHours(1),
            arrivalWindow: new ArrivalWindow(DateTimeOffset.UtcNow.AddHours(2), DateTimeOffset.UtcNow.AddHours(3)),
            ownerId: Guid.NewGuid(),
            createdAt: DateTimeOffset.UtcNow);
        if (lockedSolution.HasValue)
        {
            trip.LockSolution(lockedSolution.Value);
        }
        return trip;
    }

    private static EtaService BuildService(Trip trip, OptimisationRun? run, IHubContext<TripHub, ITripHubClient> hub)
    {
        var tripRepo = new Mock<ITripRepository>();
        tripRepo.Setup(r => r.GetByIdAsync(trip.Id, It.IsAny<CancellationToken>())).ReturnsAsync(trip);

        var runRepo = new Mock<IOptimisationRunRepository>();
        if (run is not null)
        {
            runRepo.Setup(r => r.ListForTripAsync(trip.Id, It.IsAny<CancellationToken>())).ReturnsAsync(new[] { run });
            runRepo.Setup(r => r.GetWithSolutionsAsync(run.Id, It.IsAny<CancellationToken>())).ReturnsAsync(run);
        }
        else
        {
            runRepo.Setup(r => r.ListForTripAsync(trip.Id, It.IsAny<CancellationToken>())).ReturnsAsync(Array.Empty<OptimisationRun>());
        }

        var routes = new Mock<IGoogleRoutesClient>();
        routes.Setup(r => r.ComputeRouteMatrixAsync(It.IsAny<IReadOnlyList<Point>>(), It.IsAny<IReadOnlyList<Point>>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync((IReadOnlyList<Point> origins, IReadOnlyList<Point> destinations, CancellationToken _) =>
            {
                var m = new double[origins.Count, destinations.Count];
                for (var i = 0; i < origins.Count; i++)
                    for (var j = 0; j < destinations.Count; j++)
                        m[i, j] = 5;
                return m;
            });

        var clock = new Mock<IClock>();
        clock.SetupGet(c => c.UtcNow).Returns(DateTimeOffset.UtcNow);

        return new EtaService(tripRepo.Object, runRepo.Object, routes.Object, hub, clock.Object, NullLogger<EtaService>.Instance);
    }

    /// <summary>
    /// Adapter exposing <see cref="ITripHubClient"/> so we can intercept the typed
    /// <c>EtaUpdated</c> call via the underlying <see cref="IClientProxy"/>.
    /// </summary>
    private sealed class HubClientProxy : ITripHubClient
    {
        private readonly IClientProxy _proxy;
        public HubClientProxy(IClientProxy proxy) { _proxy = proxy; }

        public Task DriverPositionUpdated(Guid driverId, double lat, double lng, DateTime ts) =>
            _proxy.SendCoreAsync("DriverPositionUpdated", new object?[] { driverId, lat, lng, ts });

        public Task EtaUpdated(Guid passengerId, DateTime newEta) =>
            _proxy.SendCoreAsync("EtaUpdated", new object?[] { passengerId, newEta });

        public Task PassengerAtStop(Guid passengerId, Guid stopId, DateTime ts) =>
            _proxy.SendCoreAsync("PassengerAtStop", new object?[] { passengerId, stopId, ts });

        public Task RouteRecomputed(Guid tripId, Guid solutionId) =>
            _proxy.SendCoreAsync("RouteRecomputed", new object?[] { tripId, solutionId });

        public Task TripStatusChanged(Guid tripId, string status) =>
            _proxy.SendCoreAsync("TripStatusChanged", new object?[] { tripId, status });
    }
}
