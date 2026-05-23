using System.Net.Http.Json;
using FluentAssertions;
using Trips.Api.Endpoints;
using Trips.Core.Contracts;
using Trips.Core.Domain;

namespace Trips.Api.Tests;

/// <summary>
/// End-to-end coverage for the runner → solver → persistence path using the real
/// <c>OrToolsSolver</c>. The default fixture swaps in a <c>StubSolver</c> whose canned
/// solution shape masks a class of EF tracking bugs (the WS4 double-add: the runner
/// attaching a Solution via both the OptimisationRun nav-collection and an explicit
/// <c>db.Solutions.Add</c>, which dies on a FK violation when the solver returns a
/// non-trivial graph). HTTP clients for TfNSW/Google/geocoding stay stubbed so the
/// test doesn't hang on real-network retries.
/// </summary>
[Collection("ApiTests")]
public sealed class RealSolverOptimisationTests : IAsyncLifetime
{
    private readonly TripsApiFactory _factory;

    public RealSolverOptimisationTests(TripsApiFactory factory)
    {
        _factory = factory;
    }

    public Task InitializeAsync() => _factory.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Optimise_with_real_OrToolsSolver_completes_and_persists_solution()
    {
        using var realFactory = _factory.WithRealSolver();
        var client = realFactory.CreateClient();
        var (authed, _) = await TripsApiFactory.RegisterAndAuthenticateAsync(
            client, "real-solver@example.com", "password123", "Real Solver");

        var trip = await CreateTripAsync(authed);
        await AddParticipantsAsync(authed, trip.Id);

        var request = new OptimiseRequest(
            new ObjectiveWeightsDto(1, 0.5, 0.5, 0.3, 0.3),
            SolverKind.OrTools);
        var post = await authed.PostAsJsonAsync($"/trips/{trip.Id}/optimise", request);
        post.EnsureSuccessStatusCode();
        var enqueue = await post.Content.ReadFromJsonAsync<EnqueueRunResponse>();
        enqueue!.RunId.Should().NotBe(Guid.Empty);

        // The synthetic 2-node matrix the runner builds solves in well under a second,
        // but CP-SAT model construction + JIT on first run can be slow; 30s is generous.
        OptimisationRunDtoWithSolution? payload = null;
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (DateTime.UtcNow < deadline)
        {
            var get = await authed.GetAsync($"/trips/{trip.Id}/runs/{enqueue.RunId}");
            get.EnsureSuccessStatusCode();
            payload = await get.Content.ReadFromJsonAsync<OptimisationRunDtoWithSolution>();
            if (payload!.Run.Status is OptimisationStatus.Completed or OptimisationStatus.Failed)
            {
                break;
            }
            await Task.Delay(250);
        }

        payload.Should().NotBeNull();
        payload!.Run.FailureReason.Should().BeNull("a FK violation surfaces as a failure reason on the run row");
        payload.Run.Status.Should().Be(OptimisationStatus.Completed);
        payload.Solution.Should().NotBeNull();
        payload.Solution!.Routes.Should().NotBeEmpty();
        payload.Run.BestSolutionId.Should().Be(payload.Solution.Id);
    }

    private static async Task<TripDto> CreateTripAsync(HttpClient client)
    {
        var trip = new CreateTripRequest(
            Name: "Real Solver Trip",
            DestinationName: "Palm Beach",
            DestinationLongitude: 151.3247,
            DestinationLatitude: -33.5984,
            DepartAt: DateTimeOffset.UtcNow.AddDays(1),
            ArrivalWindowEarliest: DateTimeOffset.UtcNow.AddDays(1).AddHours(2),
            ArrivalWindowLatest: DateTimeOffset.UtcNow.AddDays(1).AddHours(3));
        var response = await client.PostAsJsonAsync("/trips", trip);
        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<TripDto>();
        return dto!;
    }

    private static async Task AddParticipantsAsync(HttpClient client, Guid tripId)
    {
        var driver = await client.PostAsJsonAsync($"/trips/{tripId}/participants", new AddParticipantRequest(
            DisplayName: "Driver",
            HomeAddress: null,
            HomeLongitude: 151.2093,
            HomeLatitude: -33.8688,
            HasCar: true,
            Seats: 4,
            Preferences: null));
        driver.EnsureSuccessStatusCode();
        var passenger = await client.PostAsJsonAsync($"/trips/{tripId}/participants", new AddParticipantRequest(
            DisplayName: "Passenger",
            HomeAddress: null,
            HomeLongitude: 151.2100,
            HomeLatitude: -33.8700,
            HasCar: false,
            Seats: 0,
            Preferences: null));
        passenger.EnsureSuccessStatusCode();
    }
}
