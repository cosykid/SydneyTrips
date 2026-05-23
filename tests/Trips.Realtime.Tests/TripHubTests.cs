using System.Net;
using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Trips.Core.Contracts;

namespace Trips.Realtime.Tests;

[Collection("RealtimeTests")]
public sealed class TripHubTests : IAsyncLifetime
{
    private readonly RealtimeApiFactory _factory;

    public TripHubTests(RealtimeApiFactory factory)
    {
        _factory = factory;
    }

    public Task InitializeAsync() => _factory.ResetAsync();
    public Task DisposeAsync() => Task.CompletedTask;

    [Fact]
    public async Task Unauthenticated_connection_is_rejected()
    {
        await using var connection = HubClientFactory.Build(_factory, accessToken: null);

        Func<Task> act = () => connection.StartAsync();
        await act.Should().ThrowAsync<HttpRequestException>().Where(e => e.StatusCode == HttpStatusCode.Unauthorized);
    }

    [Fact]
    public async Task Authenticated_connection_succeeds()
    {
        var user = await _factory.CreateAuthenticatedUserAsync("conn@example.com");
        await using var connection = HubClientFactory.Build(_factory, user.AccessToken);

        await connection.StartAsync();
        connection.State.Should().Be(HubConnectionState.Connected);
        await connection.StopAsync();
    }

    [Fact]
    public async Task JoinTrip_returns_HubException_when_trip_unknown()
    {
        var user = await _factory.CreateAuthenticatedUserAsync("join-bad@example.com");
        await using var connection = HubClientFactory.Build(_factory, user.AccessToken);
        await connection.StartAsync();

        Func<Task> act = () => connection.InvokeAsync("JoinTripAsync", Guid.NewGuid());
        await act.Should().ThrowAsync<HubException>();
    }

    [Fact]
    public async Task JoinTrip_returns_HubException_when_user_not_participant()
    {
        var owner = await _factory.CreateAuthenticatedUserAsync("owner@example.com");
        var trip = await CreateTripAsync(owner.HttpClient, "Trip A");

        var outsider = await _factory.CreateAuthenticatedUserAsync("outsider@example.com");
        await using var connection = HubClientFactory.Build(_factory, outsider.AccessToken);
        await connection.StartAsync();

        Func<Task> act = () => connection.InvokeAsync("JoinTripAsync", trip.Id);
        await act.Should().ThrowAsync<HubException>();
    }

    [Fact]
    public async Task Driver_position_broadcast_reaches_other_group_members()
    {
        var owner = await _factory.CreateAuthenticatedUserAsync("driver-broadcast@example.com");
        var trip = await CreateTripAsync(owner.HttpClient, "Broadcast Trip");
        // Owner becomes a driver participant.
        await AddParticipantAsync(owner.HttpClient, trip.Id, "Driver", hasCar: true, seats: 4);

        var listener = await _factory.CreateAuthenticatedUserAsync("listener@example.com");
        // Listener joins as a passenger so authz lets them into the group.
        var listenerParticipant = await AddParticipantAsForOtherUserAsync(owner.HttpClient, trip.Id, "Passenger", hasCar: false, listenerUserId: listener.UserId);
        listenerParticipant.Should().NotBeNull();

        await using var driverConn = HubClientFactory.Build(_factory, owner.AccessToken);
        await using var passengerConn = HubClientFactory.Build(_factory, listener.AccessToken);

        var positionTcs = new TaskCompletionSource<(Guid DriverId, double Lat, double Lng)>();
        passengerConn.On<Guid, double, double, DateTime>("DriverPositionUpdated", (drvId, lat, lng, _) =>
            positionTcs.TrySetResult((drvId, lat, lng)));

        await driverConn.StartAsync();
        await passengerConn.StartAsync();

        await driverConn.InvokeAsync("JoinTripAsync", trip.Id);
        await passengerConn.InvokeAsync("JoinTripAsync", trip.Id);

        await driverConn.InvokeAsync("PublishDriverPositionAsync", trip.Id, -33.8688, 151.2093);

        var received = await positionTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        received.Lat.Should().Be(-33.8688);
        received.Lng.Should().Be(151.2093);
    }

    [Fact]
    public async Task Cross_trip_isolation_other_trip_does_not_receive()
    {
        var ownerA = await _factory.CreateAuthenticatedUserAsync("ownerA@example.com");
        var tripA = await CreateTripAsync(ownerA.HttpClient, "Trip A");
        await AddParticipantAsync(ownerA.HttpClient, tripA.Id, "Driver A", hasCar: true, seats: 4);

        var ownerB = await _factory.CreateAuthenticatedUserAsync("ownerB@example.com");
        var tripB = await CreateTripAsync(ownerB.HttpClient, "Trip B");
        await AddParticipantAsync(ownerB.HttpClient, tripB.Id, "Driver B", hasCar: true, seats: 4);

        await using var aConn = HubClientFactory.Build(_factory, ownerA.AccessToken);
        await using var bConn = HubClientFactory.Build(_factory, ownerB.AccessToken);
        await aConn.StartAsync();
        await bConn.StartAsync();
        await aConn.InvokeAsync("JoinTripAsync", tripA.Id);
        await bConn.InvokeAsync("JoinTripAsync", tripB.Id);

        var bReceivedAny = false;
        bConn.On<Guid, double, double, DateTime>("DriverPositionUpdated", (_, _, _, _) => bReceivedAny = true);

        // Owner-A publishes a position on tripA; owner-B should not see it.
        await aConn.InvokeAsync("PublishDriverPositionAsync", tripA.Id, -33.86, 151.21);

        // Give a little time for any mistaken cross-trip delivery to arrive.
        await Task.Delay(500);
        bReceivedAny.Should().BeFalse();
    }

    private static async Task<TripDto> CreateTripAsync(HttpClient client, string name = "Trip")
    {
        var req = new CreateTripRequest(
            Name: name,
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

    private static async Task<ParticipantDto?> AddParticipantAsync(HttpClient client, Guid tripId, string displayName, bool hasCar, int seats = 0)
    {
        var req = new AddParticipantRequest(
            DisplayName: displayName,
            HomeAddress: null,
            HomeLongitude: 151.2,
            HomeLatitude: -33.86,
            HasCar: hasCar,
            Seats: seats,
            Preferences: null);
        var response = await client.PostAsJsonAsync($"/trips/{tripId}/participants", req);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadFromJsonAsync<ParticipantDto>();
    }

    /// <summary>
    /// Force-set a participant's <c>UserId</c> to a specific identity. The standard
    /// <c>POST /trips/{id}/participants</c> endpoint uses the *caller's* user id; for tests we want
    /// the owner to add other users to the trip with their real ids so authz lets them join.
    /// </summary>
    private async Task<ParticipantDto?> AddParticipantAsForOtherUserAsync(
        HttpClient ownerClient,
        Guid tripId,
        string displayName,
        bool hasCar,
        Guid listenerUserId)
    {
        var added = await AddParticipantAsync(ownerClient, tripId, displayName, hasCar, hasCar ? 4 : 0);
        if (added is null) return null;

        // Patch the user id directly in the DB so the listener qualifies via authz.
        using var scope = _factory.Services.CreateAsyncScope();
        var db = scope.ServiceProvider.GetRequiredService<Trips.Data.TripsDbContext>();
        var entity = await db.Participants.FirstAsync(p => p.Id == added.Id);
        // Participant.UserId is private set; use EF Core property accessor to update it.
        db.Entry(entity).Property("UserId").CurrentValue = listenerUserId;
        await db.SaveChangesAsync();
        return added;
    }
}
