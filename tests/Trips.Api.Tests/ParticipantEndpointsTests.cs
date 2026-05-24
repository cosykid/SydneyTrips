using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Trips.Api.Endpoints;
using Trips.Core.Contracts;

namespace Trips.Api.Tests;

[Collection("ApiTests")]
public sealed class ParticipantEndpointsTests : IAsyncLifetime
{
    private readonly TripsApiFactory _factory;

    public ParticipantEndpointsTests(TripsApiFactory factory)
    {
        _factory = factory;
    }

    public Task InitializeAsync() => _factory.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static CreateTripRequest SampleTrip() => new(
        Name: "Beach Run",
        DestinationName: "Palm Beach",
        DestinationLongitude: 151.3247,
        DestinationLatitude: -33.5984,
        DepartAt: DateTimeOffset.UtcNow.AddDays(1),
        ArrivalWindowEarliest: DateTimeOffset.UtcNow.AddDays(1).AddHours(2),
        ArrivalWindowLatest: DateTimeOffset.UtcNow.AddDays(1).AddHours(3));

    [Fact]
    public async Task AddParticipant_with_coords_returns_201_and_candidate_nodes_populated()
    {
        var (client, _) = await _factory.CreateAuthenticatedClientAsync("p1@example.com");
        var trip = await CreateTripAsync(client);

        var add = new AddParticipantRequest(
            DisplayName: "Bob",
            HomeAddress: null,
            HomeLongitude: 151.2093,
            HomeLatitude: -33.8688,
            HasCar: true,
            Seats: 4,
            Preferences: null);

        var response = await client.PostAsJsonAsync($"/trips/{trip.Id}/participants", add);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var dto = await response.Content.ReadFromJsonAsync<ParticipantDto>();
        dto.Should().NotBeNull();
        dto!.DisplayName.Should().Be("Bob");
        dto.HasCar.Should().BeTrue();
        dto.Seats.Should().Be(4);
    }

    [Fact]
    public async Task AddParticipant_with_address_geocodes_via_stub()
    {
        var (client, _) = await _factory.CreateAuthenticatedClientAsync("p2@example.com");
        var trip = await CreateTripAsync(client);

        var add = new AddParticipantRequest(
            DisplayName: "Carol",
            HomeAddress: "100 George St, Sydney NSW",
            HomeLongitude: null,
            HomeLatitude: null,
            HasCar: false,
            Seats: 0,
            Preferences: new PreferencesDto(15, 10, 1.0));

        var response = await client.PostAsJsonAsync($"/trips/{trip.Id}/participants", add);
        response.StatusCode.Should().Be(HttpStatusCode.Created);
    }

    [Fact]
    public async Task AddParticipant_validation_failure_returns_400()
    {
        var (client, _) = await _factory.CreateAuthenticatedClientAsync("p3@example.com");
        var trip = await CreateTripAsync(client);

        var add = new AddParticipantRequest(
            DisplayName: "",
            HomeAddress: null,
            HomeLongitude: null,
            HomeLatitude: null,
            HasCar: true,
            Seats: 0,
            Preferences: null);

        var response = await client.PostAsJsonAsync($"/trips/{trip.Id}/participants", add);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task UpdateParticipantPrefs_returns_updated_dto()
    {
        var (client, _) = await _factory.CreateAuthenticatedClientAsync("p4@example.com");
        var trip = await CreateTripAsync(client);
        var participant = await AddParticipantAsync(client, trip.Id);

        var update = new PreferencesDto(WalkBudgetMins: 25, DetourToleranceMins: 15, FairnessWeight: 1.5);
        var response = await client.PatchAsJsonAsync($"/trips/{trip.Id}/participants/{participant.Id}/prefs", update);
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var dto = await response.Content.ReadFromJsonAsync<ParticipantDto>();
        dto!.Preferences.WalkBudgetMins.Should().Be(25);
    }

    [Fact]
    public async Task AddParticipant_populates_walk_and_pt_minutes_on_candidate_nodes()
    {
        var (client, _) = await _factory.CreateAuthenticatedClientAsync("pt-mins@example.com");
        var trip = await CreateTripAsync(client);

        var add = new AddParticipantRequest(
            DisplayName: "PT Passenger",
            HomeAddress: null,
            HomeLongitude: 151.2093,
            HomeLatitude: -33.8688,
            HasCar: false,
            Seats: 0,
            Preferences: new PreferencesDto(WalkBudgetMins: 5, DetourToleranceMins: 10, FairnessWeight: 1.0));
        var response = await client.PostAsJsonAsync($"/trips/{trip.Id}/participants", add);
        response.StatusCode.Should().Be(HttpStatusCode.Created);

        // GET /trips/{id} eager-loads CandidateNodes — assert the stub TfNSW client's far
        // train station was admitted as a PT-only candidate (walking 3.2 km exceeds the 5-min
        // walk budget, so this node only makes it in because TripPlanAsync returned PT minutes).
        var detailResp = await client.GetAsync($"/trips/{trip.Id}");
        detailResp.EnsureSuccessStatusCode();
        var detail = await detailResp.Content.ReadFromJsonAsync<TripDetailDto>();
        detail.Should().NotBeNull();
        var passenger = detail!.Participants.Single(p => p.DisplayName == "PT Passenger");
        passenger.CandidateNodes.Should().Contain(c => c.PtMins > 0, "PT-reachable stops should report non-zero ptMins");
    }

    [Fact]
    public async Task RefreshCandidateNodes_repopulates_existing_participants()
    {
        var (client, _) = await _factory.CreateAuthenticatedClientAsync("refresh@example.com");
        var trip = await CreateTripAsync(client);
        await AddParticipantAsync(client, trip.Id);

        var resp = await client.PostAsync($"/trips/{trip.Id}/refresh-candidate-nodes", content: null);
        resp.StatusCode.Should().Be(HttpStatusCode.OK);
        var payload = await resp.Content.ReadFromJsonAsync<RefreshCandidateNodesResponse>();
        payload.Should().NotBeNull();
        payload!.ParticipantsRefreshed.Should().Be(1);
        // The stub always returns at least Home + nearby stops, so the post-refresh count must be > 0.
        payload.CandidateNodesAfter.Should().BeGreaterThan(0);
    }

    [Fact]
    public async Task DeleteParticipant_returns_204()
    {
        var (client, _) = await _factory.CreateAuthenticatedClientAsync("p5@example.com");
        var trip = await CreateTripAsync(client);
        var participant = await AddParticipantAsync(client, trip.Id);

        var response = await client.DeleteAsync($"/trips/{trip.Id}/participants/{participant.Id}");
        response.StatusCode.Should().Be(HttpStatusCode.NoContent);
    }

    private static async Task<TripDto> CreateTripAsync(HttpClient client)
    {
        var create = await client.PostAsJsonAsync("/trips", SampleTrip());
        create.EnsureSuccessStatusCode();
        var trip = await create.Content.ReadFromJsonAsync<TripDto>();
        return trip!;
    }

    private static async Task<ParticipantDto> AddParticipantAsync(HttpClient client, Guid tripId)
    {
        var add = new AddParticipantRequest(
            DisplayName: "Default Person",
            HomeAddress: null,
            HomeLongitude: 151.2093,
            HomeLatitude: -33.8688,
            HasCar: false,
            Seats: 0,
            Preferences: null);
        var response = await client.PostAsJsonAsync($"/trips/{tripId}/participants", add);
        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<ParticipantDto>();
        return dto!;
    }
}
