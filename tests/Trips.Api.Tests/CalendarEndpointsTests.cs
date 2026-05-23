using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Trips.Core.Contracts;

namespace Trips.Api.Tests;

[Collection("ApiTests")]
public sealed class CalendarEndpointsTests : IAsyncLifetime
{
    private readonly TripsApiFactory _factory;

    public CalendarEndpointsTests(TripsApiFactory factory)
    {
        _factory = factory;
    }

    public Task InitializeAsync() => _factory.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Calendar_returns_404_without_locked_solution()
    {
        var (client, _) = await _factory.CreateAuthenticatedClientAsync("cal0@example.com");
        var (tripId, participantId) = await SetupAsync(client);

        var response = await client.GetAsync($"/trips/{tripId}/participants/{participantId}/calendar.ics");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Calendar_returns_text_calendar_with_VEVENT()
    {
        var (client, _) = await _factory.CreateAuthenticatedClientAsync("cal1@example.com");
        var (tripId, participantId) = await SetupAsync(client);
        await OptimiseAndLockAsync(client, tripId);

        var response = await client.GetAsync($"/trips/{tripId}/participants/{participantId}/calendar.ics");
        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType?.MediaType.Should().Be("text/calendar");
        var text = await response.Content.ReadAsStringAsync();
        text.Should().Contain("BEGIN:VCALENDAR");
        text.Should().Contain("BEGIN:VEVENT");
        text.Should().Contain("END:VEVENT");
        text.Should().Contain("END:VCALENDAR");
    }

    [Fact]
    public async Task Calendar_blocks_non_participant_caller()
    {
        var (ownerClient, _) = await _factory.CreateAuthenticatedClientAsync("cal2-owner@example.com");
        var (tripId, driverId) = await SetupAsync(ownerClient);
        await OptimiseAndLockAsync(ownerClient, tripId);

        // Another user who is NOT the trip owner nor any participant of this trip.
        var (otherClient, _) = await _factory.CreateAuthenticatedClientAsync("cal2-other@example.com");
        var response = await otherClient.GetAsync($"/trips/{tripId}/participants/{driverId}/calendar.ics");
        // TripAuthorizationService treats non-owner/non-participant as 404 to avoid leaking trip existence.
        response.StatusCode.Should().BeOneOf(HttpStatusCode.NotFound, HttpStatusCode.Forbidden);
    }

    private static async Task<(Guid TripId, Guid ParticipantId)> SetupAsync(HttpClient client)
    {
        var trip = await client.PostAsJsonAsync("/trips", new CreateTripRequest(
            Name: "Cal Trip",
            DestinationName: "Palm Beach",
            DestinationLongitude: 151.3247,
            DestinationLatitude: -33.5984,
            DepartAt: DateTimeOffset.UtcNow.AddDays(1),
            ArrivalWindowEarliest: DateTimeOffset.UtcNow.AddDays(1).AddHours(2),
            ArrivalWindowLatest: DateTimeOffset.UtcNow.AddDays(1).AddHours(3)));
        trip.EnsureSuccessStatusCode();
        var tripDto = await trip.Content.ReadFromJsonAsync<TripDto>();

        // Driver (this is the authenticated user — UserId matches the trip owner).
        var driverResp = await client.PostAsJsonAsync($"/trips/{tripDto!.Id}/participants", new AddParticipantRequest(
            DisplayName: "Driver",
            HomeAddress: null,
            HomeLongitude: 151.2093,
            HomeLatitude: -33.8688,
            HasCar: true,
            Seats: 4,
            Preferences: null));
        driverResp.EnsureSuccessStatusCode();
        var driver = await driverResp.Content.ReadFromJsonAsync<ParticipantDto>();

        await client.PostAsJsonAsync($"/trips/{tripDto.Id}/participants", new AddParticipantRequest(
            DisplayName: "Passenger",
            HomeAddress: null,
            HomeLongitude: 151.2100,
            HomeLatitude: -33.8700,
            HasCar: false,
            Seats: 0,
            Preferences: null));
        return (tripDto.Id, driver!.Id);
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
