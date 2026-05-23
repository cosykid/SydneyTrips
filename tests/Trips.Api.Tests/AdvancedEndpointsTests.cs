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
    public async Task WhatIf_without_locked_solution_returns_409()
    {
        var (client, _) = await _factory.CreateAuthenticatedClientAsync("whatif0@example.com");
        var trip = await CreateTripWithParticipantsAsync(client);

        var what = new WhatIfRequest(
            DropParticipantIds: null,
            AddParticipants: null,
            NewWeights: new ObjectiveWeightsDto(0.5, 0.2, 1.0, 0.3, 0.3));
        var response = await client.PostAsJsonAsync($"/trips/{trip.Id}/whatif", what);
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task WhatIf_after_lock_enqueues_a_run()
    {
        var (client, _) = await _factory.CreateAuthenticatedClientAsync("whatif1@example.com");
        var trip = await CreateTripWithParticipantsAsync(client);
        await OptimiseAndLockAsync(client, trip.Id);

        var what = new WhatIfRequest(
            DropParticipantIds: null,
            AddParticipants: null,
            NewWeights: new ObjectiveWeightsDto(0.5, 0.2, 1.0, 0.3, 0.3));
        var response = await client.PostAsJsonAsync($"/trips/{trip.Id}/whatif", what);
        response.StatusCode.Should().Be(HttpStatusCode.Accepted);
    }

    [Fact]
    public async Task CostSplit_without_locked_solution_returns_409()
    {
        var (client, _) = await _factory.CreateAuthenticatedClientAsync("cost0@example.com");
        var trip = await CreateTripWithParticipantsAsync(client);

        var response = await client.GetAsync($"/trips/{trip.Id}/cost-split");
        response.StatusCode.Should().Be(HttpStatusCode.Conflict);
    }

    [Fact]
    public async Task CostSplit_after_lock_returns_itemised_breakdown()
    {
        var (client, _) = await _factory.CreateAuthenticatedClientAsync("cost1@example.com");
        var trip = await CreateTripWithParticipantsAsync(client);
        await OptimiseAndLockAsync(client, trip.Id);

        var response = await client.GetFromJsonAsync<CostSplitResponse>($"/trips/{trip.Id}/cost-split");
        response.Should().NotBeNull();
        response!.SolutionId.Should().NotBeNull();
        response.FuelPricePerLitre.Should().BeGreaterThan(0);
        response.FuelEconomyLPer100Km.Should().BeGreaterThan(0);
        // The stub solver pickups everyone at a single Sydney CBD point — total cost may be small
        // (haversine driver→stop only) but the contract must be populated.
        response.Entries.Should().NotBeNull();
        response.TotalCost.Should().Be(response.TotalFuel + response.TotalTolls);
    }

    [Fact]
    public async Task CostSplit_accepts_override_inputs()
    {
        var (client, _) = await _factory.CreateAuthenticatedClientAsync("cost2@example.com");
        var trip = await CreateTripWithParticipantsAsync(client);
        await OptimiseAndLockAsync(client, trip.Id);

        var inputs = new CostSplitInputsDto(
            FuelPricePerLitre: 1.99,
            FuelEconomyLPer100Km: 7.5,
            Tolls: Array.Empty<TollSegmentDto>());
        var response = await client.PostAsJsonAsync($"/trips/{trip.Id}/cost-split", inputs);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await response.Content.ReadFromJsonAsync<CostSplitResponse>();
        payload!.FuelPricePerLitre.Should().Be(1.99);
        payload.FuelEconomyLPer100Km.Should().Be(7.5);
    }

    [Fact]
    public async Task ReturnLeg_with_requests_returns_solutions()
    {
        var (client, _) = await _factory.CreateAuthenticatedClientAsync("ret1@example.com");
        var trip = await CreateTripWithParticipantsAsync(client);

        var body = new ReturnLegRequest(new[]
        {
            new ReturnRequestDto(Guid.NewGuid(), DateTime.UtcNow.AddHours(8), 151.21, -33.87),
            new ReturnRequestDto(Guid.NewGuid(), DateTime.UtcNow.AddHours(8).AddMinutes(5), 151.22, -33.88),
        });
        var response = await client.PostAsJsonAsync($"/trips/{trip.Id}/return-leg", body);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await response.Content.ReadFromJsonAsync<ReturnLegResponse>();
        dto!.Solutions.Should().NotBeEmpty();
    }

    [Fact]
    public async Task ReturnLeg_with_empty_request_returns_400()
    {
        var (client, _) = await _factory.CreateAuthenticatedClientAsync("ret2@example.com");
        var trip = await CreateTripWithParticipantsAsync(client);

        var body = new ReturnLegRequest(Array.Empty<ReturnRequestDto>());
        var response = await client.PostAsJsonAsync($"/trips/{trip.Id}/return-leg", body);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
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

    private static async Task OptimiseAndLockAsync(HttpClient client, Guid tripId)
    {
        var post = await client.PostAsJsonAsync($"/trips/{tripId}/optimise", new OptimiseRequest(new ObjectiveWeightsDto(1, 0.5, 0.5, 0.3, 0.3)));
        var enq = await post.Content.ReadFromJsonAsync<EnqueueRunResponse>();
        await OptimisationEndpointsTests.PollUntilCompletedAsync(client, tripId, enq!.RunId);
        var lockResp = await client.PostAsJsonAsync($"/trips/{tripId}/lock-solution",
            new LockSolutionRequest(enq.RunId, ParetoIndex: 0));
        lockResp.EnsureSuccessStatusCode();
    }
}
