using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Trips.Api.Endpoints;
using Trips.Core.Contracts;

namespace Trips.Api.Tests;

[Collection("ApiTests")]
public sealed class AdvancedEndpointsTests : IAsyncLifetime
{
    private readonly TripsApiFactory _factory;

    public AdvancedEndpointsTests(TripsApiFactory factory)
    {
        _factory = factory;
    }

    public Task InitializeAsync() => _factory.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task LockSolution_marks_trip_lockedSolutionId()
    {
        var (client, _) = await _factory.CreateAuthenticatedClientAsync("lock1@example.com");
        var trip = await CreateTripWithParticipantsAsync(client);

        var post = await client.PostAsJsonAsync($"/trips/{trip.Id}/optimise", new OptimiseRequest(new ObjectiveWeightsDto(1, 0.5, 0.5, 0.3, 0.3)));
        var enq = await post.Content.ReadFromJsonAsync<EnqueueRunResponse>();
        await OptimisationEndpointsTests.PollUntilCompletedAsync(client, trip.Id, enq!.RunId);

        var lockResponse = await client.PostAsJsonAsync($"/trips/{trip.Id}/lock-solution", new LockSolutionRequest(enq.RunId, ParetoIndex: 0));
        lockResponse.StatusCode.Should().Be(HttpStatusCode.OK);
        var refreshed = await lockResponse.Content.ReadFromJsonAsync<TripDto>();
        refreshed!.LockedSolutionId.Should().NotBeNull();
    }

    [Fact]
    public async Task LockSolution_with_bad_pareto_index_returns_400()
    {
        var (client, _) = await _factory.CreateAuthenticatedClientAsync("lock2@example.com");
        var trip = await CreateTripWithParticipantsAsync(client);

        var post = await client.PostAsJsonAsync($"/trips/{trip.Id}/optimise", new OptimiseRequest(new ObjectiveWeightsDto(1, 0.5, 0.5, 0.3, 0.3)));
        var enq = await post.Content.ReadFromJsonAsync<EnqueueRunResponse>();
        await OptimisationEndpointsTests.PollUntilCompletedAsync(client, trip.Id, enq!.RunId);

        var lockResponse = await client.PostAsJsonAsync($"/trips/{trip.Id}/lock-solution", new LockSolutionRequest(enq.RunId, ParetoIndex: 99));
        lockResponse.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task WhatIf_enqueues_a_run()
    {
        var (client, _) = await _factory.CreateAuthenticatedClientAsync("whatif1@example.com");
        var trip = await CreateTripWithParticipantsAsync(client);

        var what = new WhatIfRequest(
            DropParticipantIds: null,
            AddParticipants: null,
            NewWeights: new ObjectiveWeightsDto(0.5, 0.2, 1.0, 0.3, 0.3));
        var response = await client.PostAsJsonAsync($"/trips/{trip.Id}/whatif", what);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task CostSplit_returns_placeholder_breakdown()
    {
        var (client, _) = await _factory.CreateAuthenticatedClientAsync("cost1@example.com");
        var trip = await CreateTripWithParticipantsAsync(client);

        var response = await client.GetFromJsonAsync<CostSplitResponse>($"/trips/{trip.Id}/cost-split");
        response.Should().NotBeNull();
        response!.Entries.Should().NotBeEmpty();
        response.TotalCost.Should().Be(0.0);
        response.Todo.Should().NotBeNullOrWhiteSpace();
    }

    [Fact]
    public async Task ReturnLeg_returns_accepted_with_run_id()
    {
        var (client, _) = await _factory.CreateAuthenticatedClientAsync("ret1@example.com");
        var trip = await CreateTripWithParticipantsAsync(client);

        var response = await client.PostAsync($"/trips/{trip.Id}/return-leg", content: null);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
        var enq = await response.Content.ReadFromJsonAsync<EnqueueRunResponse>();
        enq!.RunId.Should().NotBe(Guid.Empty);
    }

    private static async Task<TripDto> CreateTripWithParticipantsAsync(HttpClient client)
    {
        var trip = await client.PostAsJsonAsync("/trips", new CreateTripRequest(
            Name: "Adv Trip",
            DestinationName: "Palm Beach",
            DestinationLongitude: 151.3247,
            DestinationLatitude: -33.5984,
            DepartAt: DateTimeOffset.UtcNow.AddDays(1),
            ArrivalWindowEarliest: DateTimeOffset.UtcNow.AddDays(1).AddHours(2),
            ArrivalWindowLatest: DateTimeOffset.UtcNow.AddDays(1).AddHours(3)));
        trip.EnsureSuccessStatusCode();
        var tripDto = await trip.Content.ReadFromJsonAsync<TripDto>();

        await client.PostAsJsonAsync($"/trips/{tripDto!.Id}/participants", new AddParticipantRequest(
            DisplayName: "Driver",
            HomeAddress: null,
            HomeLongitude: 151.2093,
            HomeLatitude: -33.8688,
            HasCar: true,
            Seats: 4,
            Preferences: null));
        await client.PostAsJsonAsync($"/trips/{tripDto.Id}/participants", new AddParticipantRequest(
            DisplayName: "Passenger",
            HomeAddress: null,
            HomeLongitude: 151.2100,
            HomeLatitude: -33.8700,
            HasCar: false,
            Seats: 0,
            Preferences: null));
        return tripDto;
    }
}
