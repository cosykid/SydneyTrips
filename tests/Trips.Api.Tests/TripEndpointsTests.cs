using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Trips.Core.Contracts;

namespace Trips.Api.Tests;

[Collection("ApiTests")]
public sealed class TripEndpointsTests : IAsyncLifetime
{
    private readonly TripsApiFactory _factory;

    public TripEndpointsTests(TripsApiFactory factory)
    {
        _factory = factory;
    }

    public Task InitializeAsync() => _factory.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    private static CreateTripRequest SampleTrip(string name = "Palm Beach Run") => new(
        Name: name,
        DestinationName: "Palm Beach",
        DestinationLongitude: 151.3247,
        DestinationLatitude: -33.5984,
        DepartAt: new DateTimeOffset(2026, 7, 1, 8, 0, 0, TimeSpan.Zero),
        ArrivalWindowEarliest: new DateTimeOffset(2026, 7, 1, 9, 30, 0, TimeSpan.Zero),
        ArrivalWindowLatest: new DateTimeOffset(2026, 7, 1, 10, 30, 0, TimeSpan.Zero));

    [Fact]
    public async Task Unauthenticated_request_is_rejected()
    {
        var client = _factory.CreateClient();
        var response = await client.GetAsync("/trips");
        response.StatusCode.Should().Be(HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task CreateTrip_returns_201_with_dto()
    {
        var (client, _) = await _factory.CreateAuthenticatedClientAsync("create@example.com");

        var response = await client.PostAsJsonAsync("/trips", SampleTrip());
        response.StatusCode.Should().Be(HttpStatusCode.Created);
        var trip = await response.Content.ReadFromJsonAsync<TripDto>();
        trip.Should().NotBeNull();
        trip!.Name.Should().Be("Palm Beach Run");
        trip.DestinationLongitude.Should().Be(151.3247);
    }

    [Fact]
    public async Task CreateTrip_with_invalid_payload_returns_400()
    {
        var (client, _) = await _factory.CreateAuthenticatedClientAsync("invalid@example.com");

        var bad = SampleTrip() with
        {
            DestinationLatitude = 999,
            Name = "",
        };
        var response = await client.PostAsJsonAsync("/trips", bad);
        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }

    [Fact]
    public async Task ListTrips_returns_only_owner_trips()
    {
        var (client, _) = await _factory.CreateAuthenticatedClientAsync("alice@example.com");

        await client.PostAsJsonAsync("/trips", SampleTrip("A"));
        await client.PostAsJsonAsync("/trips", SampleTrip("B"));

        var listed = await client.GetFromJsonAsync<TripDto[]>("/trips");
        listed.Should().NotBeNull();
        listed!.Should().HaveCount(2);
    }

    [Fact]
    public async Task GetTrip_returns_404_when_caller_unauthorized()
    {
        var (clientA, _) = await _factory.CreateAuthenticatedClientAsync("a@example.com", displayName: "A");
        var created = await clientA.PostAsJsonAsync("/trips", SampleTrip());
        created.EnsureSuccessStatusCode();
        var trip = await created.Content.ReadFromJsonAsync<TripDto>();

        var (clientB, _) = await _factory.CreateAuthenticatedClientAsync("b@example.com", displayName: "B");
        var response = await clientB.GetAsync($"/trips/{trip!.Id}");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task GetTrip_returns_trip_when_owner()
    {
        var (client, _) = await _factory.CreateAuthenticatedClientAsync("owner@example.com");

        var created = await client.PostAsJsonAsync("/trips", SampleTrip());
        var trip = await created.Content.ReadFromJsonAsync<TripDto>();

        var fetched = await client.GetFromJsonAsync<TripDto>($"/trips/{trip!.Id}");
        fetched.Should().NotBeNull();
        fetched!.Id.Should().Be(trip.Id);
    }

    [Fact]
    public async Task DeleteTrip_returns_204()
    {
        var (client, _) = await _factory.CreateAuthenticatedClientAsync("delete@example.com");

        var created = await client.PostAsJsonAsync("/trips", SampleTrip());
        var trip = await created.Content.ReadFromJsonAsync<TripDto>();

        var deleted = await client.DeleteAsync($"/trips/{trip!.Id}");
        deleted.StatusCode.Should().Be(HttpStatusCode.NoContent);

        var fetch = await client.GetAsync($"/trips/{trip.Id}");
        fetch.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
