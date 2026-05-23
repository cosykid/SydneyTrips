using System.Globalization;
using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Trips.Core.Contracts;
using Trips.Core.Domain;

namespace Trips.Api.Tests;

[Collection("ApiTests")]
public sealed class EventsEndpointsTests : IAsyncLifetime
{
    private readonly TripsApiFactory _factory;

    public EventsEndpointsTests(TripsApiFactory factory)
    {
        _factory = factory;
    }

    public Task InitializeAsync() => _factory.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task ListEvents_returns_TripCreated_for_owner()
    {
        var (client, _) = await _factory.CreateAuthenticatedClientAsync("evt-owner@example.com");
        var trip = await CreateTripAsync(client);

        var response = await client.GetAsync($"/trips/{trip.Id}/events");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var events = await response.Content.ReadFromJsonAsync<TripEventDto[]>();
        events.Should().NotBeNull();
        events!.Should().Contain(e => e.Kind == EventKind.TripCreated);
        events.Should().OnlyContain(e => e.TripId == trip.Id);
    }

    [Fact]
    public async Task ListEvents_returns_404_when_caller_not_a_participant()
    {
        var (ownerClient, _) = await _factory.CreateAuthenticatedClientAsync("evt-a@example.com");
        var trip = await CreateTripAsync(ownerClient);

        var (outsider, _) = await _factory.CreateAuthenticatedClientAsync("evt-b@example.com");
        var response = await outsider.GetAsync($"/trips/{trip.Id}/events");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task ListEvents_filters_by_since_when_provided()
    {
        var (client, _) = await _factory.CreateAuthenticatedClientAsync("evt-since@example.com");
        var trip = await CreateTripAsync(client);

        // Use a since timestamp in the future — should return zero events.
        var future = DateTimeOffset.UtcNow.AddDays(7).ToString("o", CultureInfo.InvariantCulture);
        var response = await client.GetAsync($"/trips/{trip.Id}/events?since={Uri.EscapeDataString(future)}");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        var events = await response.Content.ReadFromJsonAsync<TripEventDto[]>();
        events!.Should().BeEmpty();
    }

    private static async Task<TripDto> CreateTripAsync(HttpClient client)
    {
        var req = new CreateTripRequest(
            Name: "Events Trip",
            DestinationName: "Palm Beach",
            DestinationLongitude: 151.3247,
            DestinationLatitude: -33.5984,
            DepartAt: DateTimeOffset.UtcNow.AddDays(1),
            ArrivalWindowEarliest: DateTimeOffset.UtcNow.AddDays(1).AddHours(2),
            ArrivalWindowLatest: DateTimeOffset.UtcNow.AddDays(1).AddHours(3));
        var response = await client.PostAsJsonAsync("/trips", req);
        response.EnsureSuccessStatusCode();
        var dto = await response.Content.ReadFromJsonAsync<TripDto>();
        return dto!;
    }
}
