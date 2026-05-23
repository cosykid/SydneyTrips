using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Trips.Api.Endpoints;
using Trips.Core.Contracts;
using Trips.Core.Domain;

namespace Trips.Api.Tests;

[Collection("ApiTests")]
public sealed class OptimisationEndpointsTests : IAsyncLifetime
{
    private readonly TripsApiFactory _factory;

    public OptimisationEndpointsTests(TripsApiFactory factory)
    {
        _factory = factory;
    }

    public Task InitializeAsync() => _factory.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Optimise_returns_202_with_run_id()
    {
        var (client, _) = await _factory.CreateAuthenticatedClientAsync("opt1@example.com");
        var trip = await CreateTripAsync(client);
        await AddParticipantsAsync(client, trip.Id);

        var request = new OptimiseRequest(new ObjectiveWeightsDto(1, 0.5, 0.5, 0.3, 0.3));
        var response = await client.PostAsJsonAsync($"/trips/{trip.Id}/optimise", request);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var enqueue = await response.Content.ReadFromJsonAsync<EnqueueRunResponse>();
        enqueue!.RunId.Should().NotBe(Guid.Empty);
    }

    [Fact]
    public async Task GetRun_polls_until_completed_and_returns_solution()
    {
        var (client, _) = await _factory.CreateAuthenticatedClientAsync("opt2@example.com");
        var trip = await CreateTripAsync(client);
        await AddParticipantsAsync(client, trip.Id);

        var request = new OptimiseRequest(new ObjectiveWeightsDto(1, 0.5, 0.5, 0.3, 0.3));
        var post = await client.PostAsJsonAsync($"/trips/{trip.Id}/optimise", request);
        var enq = await post.Content.ReadFromJsonAsync<EnqueueRunResponse>();
        enq!.RunId.Should().NotBe(Guid.Empty);

        OptimisationRunDtoWithSolution? payload = null;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var get = await client.GetAsync($"/trips/{trip.Id}/runs/{enq.RunId}");
            get.EnsureSuccessStatusCode();
            payload = await get.Content.ReadFromJsonAsync<OptimisationRunDtoWithSolution>();
            if (payload!.Run.Status == OptimisationStatus.Completed)
            {
                break;
            }
            await Task.Delay(200);
        }

        payload.Should().NotBeNull();
        payload!.Run.Status.Should().Be(OptimisationStatus.Completed);
        payload.Solution.Should().NotBeNull();
        payload.Solution!.Routes.Should().NotBeNull();
    }

    [Fact]
    public async Task GetPareto_returns_three_solutions()
    {
        var (client, _) = await _factory.CreateAuthenticatedClientAsync("opt3@example.com");
        var trip = await CreateTripAsync(client);
        await AddParticipantsAsync(client, trip.Id);

        var post = await client.PostAsJsonAsync($"/trips/{trip.Id}/optimise", new OptimiseRequest(new ObjectiveWeightsDto(1, 0.5, 0.5, 0.3, 0.3)));
        var enq = await post.Content.ReadFromJsonAsync<EnqueueRunResponse>();

        // Wait for run to complete so solutions are populated.
        await PollUntilCompletedAsync(client, trip.Id, enq!.RunId);

        var pareto = await client.GetFromJsonAsync<SolutionDto[]>($"/trips/{trip.Id}/runs/{enq.RunId}/pareto");
        Assert.NotNull(pareto);
        pareto.Should().HaveCount(3);
        pareto.Select(p => p.Label).Should().BeEquivalentTo(new[] { "fastest", "fewest-stops", "least-walking" });
    }

    [Fact]
    public async Task Optimise_validates_weights()
    {
        var (client, _) = await _factory.CreateAuthenticatedClientAsync("opt4@example.com");
        var trip = await CreateTripAsync(client);

        var bad = new OptimiseRequest(new ObjectiveWeightsDto(-1, 0, 0, 0, 0));
        var response = await client.PostAsJsonAsync($"/trips/{trip.Id}/optimise", bad);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    private static async Task<TripDto> CreateTripAsync(HttpClient client)
    {
        var trip = new CreateTripRequest(
            Name: "Optimisation Trip",
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
        await client.PostAsJsonAsync($"/trips/{tripId}/participants", new AddParticipantRequest(
            DisplayName: "Driver",
            HomeAddress: null,
            HomeLongitude: 151.2093,
            HomeLatitude: -33.8688,
            HasCar: true,
            Seats: 4,
            Preferences: null));
        await client.PostAsJsonAsync($"/trips/{tripId}/participants", new AddParticipantRequest(
            DisplayName: "Passenger",
            HomeAddress: null,
            HomeLongitude: 151.2100,
            HomeLatitude: -33.8700,
            HasCar: false,
            Seats: 0,
            Preferences: null));
    }

    internal static async Task PollUntilCompletedAsync(HttpClient client, Guid tripId, Guid runId)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            var payload = await client.GetFromJsonAsync<OptimisationRunDtoWithSolution>($"/trips/{tripId}/runs/{runId}");
            if (payload!.Run.Status is OptimisationStatus.Completed or OptimisationStatus.Failed)
            {
                return;
            }
            await Task.Delay(200);
        }
        throw new TimeoutException($"Run {runId} did not finish in time");
    }
}
