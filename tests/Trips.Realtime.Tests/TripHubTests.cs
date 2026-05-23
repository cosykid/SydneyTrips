using System.Net.Http.Json;
using FluentAssertions;
using Microsoft.AspNetCore.SignalR;
using Microsoft.AspNetCore.SignalR.Client;
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
    public async Task Anonymous_connection_succeeds()
    {
        // No auth required — anonymous cookies flow on negotiate.
        await using var connection = HubClientFactory.Build(_factory);

        await connection.StartAsync();
        connection.State.Should().Be(HubConnectionState.Connected);
        await connection.StopAsync();
    }

    [Fact]
    public async Task JoinTrip_returns_HubException_when_trip_unknown()
    {
        await using var connection = HubClientFactory.Build(_factory);
        await connection.StartAsync();

        Func<Task> act = () => connection.InvokeAsync("JoinTripAsync", Guid.NewGuid());
        await act.Should().ThrowAsync<HubException>();
    }

    [Fact]
    public async Task Driver_position_broadcast_reaches_other_group_members()
    {
        var owner = await _factory.CreateClientSessionAsync();
        var trip = await CreateTripAsync(owner.HttpClient, "Broadcast Trip");
        var driver = await AddParticipantAsync(owner.HttpClient, trip.Id, "Driver", hasCar: true, seats: 4);

        var listenerClient = await _factory.CreateClientSessionAsync();

        await using var driverConn = HubClientFactory.Build(_factory, owner);
        await using var passengerConn = HubClientFactory.Build(_factory, listenerClient);

        var positionTcs = new TaskCompletionSource<(Guid DriverId, double Lat, double Lng)>();
        passengerConn.On<Guid, double, double, DateTime>("DriverPositionUpdated", (drvId, lat, lng, _) =>
            positionTcs.TrySetResult((drvId, lat, lng)));

        await driverConn.StartAsync();
        await passengerConn.StartAsync();

        await driverConn.InvokeAsync("JoinTripAsync", trip.Id);
        await passengerConn.InvokeAsync("JoinTripAsync", trip.Id);

        await driverConn.InvokeAsync("PublishDriverPositionAsync", trip.Id, driver!.Id, -33.8688, 151.2093);

        var received = await positionTcs.Task.WaitAsync(TimeSpan.FromSeconds(5));
        received.Lat.Should().Be(-33.8688);
        received.Lng.Should().Be(151.2093);
    }

    [Fact]
    public async Task Cross_trip_isolation_other_trip_does_not_receive()
    {
        var ownerA = await _factory.CreateClientSessionAsync();
        var tripA = await CreateTripAsync(ownerA.HttpClient, "Trip A");
        var driverA = await AddParticipantAsync(ownerA.HttpClient, tripA.Id, "Driver A", hasCar: true, seats: 4);

        var ownerB = await _factory.CreateClientSessionAsync();
        var tripB = await CreateTripAsync(ownerB.HttpClient, "Trip B");
        await AddParticipantAsync(ownerB.HttpClient, tripB.Id, "Driver B", hasCar: true, seats: 4);

        await using var aConn = HubClientFactory.Build(_factory, ownerA);
        await using var bConn = HubClientFactory.Build(_factory, ownerB);
        await aConn.StartAsync();
        await bConn.StartAsync();
        await aConn.InvokeAsync("JoinTripAsync", tripA.Id);
        await bConn.InvokeAsync("JoinTripAsync", tripB.Id);

        var bReceivedAny = false;
        bConn.On<Guid, double, double, DateTime>("DriverPositionUpdated", (_, _, _, _) => bReceivedAny = true);

        // Owner-A publishes a position on tripA; owner-B should not see it (different SignalR group).
        await aConn.InvokeAsync("PublishDriverPositionAsync", tripA.Id, driverA!.Id, -33.86, 151.21);

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
}
