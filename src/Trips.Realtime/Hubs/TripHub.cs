using System.Text.Json;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;
using Trips.Core.Abstractions;
using Trips.Core.Domain;
using Trips.Realtime.Eta;
using Trips.Realtime.Gtfs;

namespace Trips.Realtime.Hubs;

/// <summary>
/// SignalR hub coordinating live driver positions, passenger check-ins, and ETA updates per trip.
/// Connections join one SignalR group per trip id (<c>trip:{tripId}</c>) so server-side broadcasts
/// fan out to exactly the right set of clients.
///
/// With anonymous-session auth there is no per-user identity on the hub; clients pass their
/// <c>participantId</c> on each invocation. The connection still flows through the
/// <c>trips_session</c> cookie (SignalR's negotiate request carries cookies natively), so it
/// inherits the same anonymous-share-link trust model the HTTP endpoints use.
/// </summary>
public sealed class TripHub : Hub<ITripHubClient>
{
    private readonly ITripHubAuthorizer _trips;
    private readonly ITripEventRepository _events;
    private readonly IParticipantRepository _participants;
    private readonly EtaRecomputeQueue _etaQueue;
    private readonly IActiveTripRegistry _activeTrips;
    private readonly IClock _clock;
    private readonly ILogger<TripHub> _logger;

    public TripHub(
        ITripHubAuthorizer trips,
        ITripEventRepository events,
        IParticipantRepository participants,
        EtaRecomputeQueue etaQueue,
        IActiveTripRegistry activeTrips,
        IClock clock,
        ILogger<TripHub> logger)
    {
        ArgumentNullException.ThrowIfNull(trips);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(participants);
        ArgumentNullException.ThrowIfNull(etaQueue);
        ArgumentNullException.ThrowIfNull(activeTrips);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _trips = trips;
        _events = events;
        _participants = participants;
        _etaQueue = etaQueue;
        _activeTrips = activeTrips;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>Stable group naming used by both the hub and <see cref="EtaService"/>.</summary>
    public static string GroupName(Guid tripId) => $"trip:{tripId}";

    /// <summary>
    /// Join the SignalR group for <paramref name="tripId"/>. Anyone connected via the share-link
    /// can join; we only verify the trip exists. Throws <see cref="HubException"/> for an unknown
    /// trip — surfaces to the client as a rejected invocation.
    /// </summary>
    public async Task JoinTripAsync(Guid tripId)
    {
        var trip = await _trips.LookupAsync(tripId, Context.ConnectionAborted).ConfigureAwait(false);
        if (trip is null)
        {
            throw new HubException("Trip not found.");
        }
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(tripId), Context.ConnectionAborted).ConfigureAwait(false);
        _activeTrips.OnConnectionJoined(tripId, Context.ConnectionId);
        _logger.LogInformation("Connection {Conn} joined trip {Trip}", Context.ConnectionId, tripId);
    }

    /// <summary>Leave the SignalR group for the trip. Idempotent.</summary>
    public async Task LeaveTripAsync(Guid tripId)
    {
        await Groups.RemoveFromGroupAsync(Context.ConnectionId, GroupName(tripId), Context.ConnectionAborted).ConfigureAwait(false);
        _activeTrips.OnConnectionLeft(tripId, Context.ConnectionId);
    }

    /// <inheritdoc />
    public override Task OnDisconnectedAsync(Exception? exception)
    {
        // Best-effort: SignalR doesn't tell us which group a connection was in, so we ask the
        // registry to remove this connection from every trip it knows about. The registry's
        // <see cref="IActiveTripRegistry.OnConnectionLeft"/> is idempotent over missing keys.
        foreach (var tripId in _activeTrips.GetActiveTrips())
        {
            _activeTrips.OnConnectionLeft(tripId, Context.ConnectionId);
        }
        return base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// Driver client publishes a fresh GPS position for <paramref name="driverParticipantId"/>.
    /// The hub
    /// (a) persists a <see cref="EventKind.DriverPositionUpdated"/> event row,
    /// (b) broadcasts <c>DriverPositionUpdated</c> to the trip's group, and
    /// (c) enqueues an ETA recompute job so passengers see refreshed arrival times.
    /// The supplied participant id must belong to <paramref name="tripId"/> and have
    /// <see cref="Participant.HasCar"/> = true. No further auth — anonymous share-link trust model.
    /// </summary>
    public async Task PublishDriverPositionAsync(Guid tripId, Guid driverParticipantId, double lat, double lng)
    {
        var trip = await _trips.LookupAsync(tripId, Context.ConnectionAborted).ConfigureAwait(false);
        if (trip is null)
        {
            throw new HubException("Trip not found.");
        }

        var driver = await ResolveDriverAsync(tripId, driverParticipantId, Context.ConnectionAborted).ConfigureAwait(false);
        if (driver is null)
        {
            throw new HubException("Driver participant not found, or that participant is not a driver.");
        }

        var ts = _clock.UtcNow;
        var factory = new GeometryFactory(new PrecisionModel(), 4326);

        await _events.AddAsync(new TripEvent(
            id: Guid.NewGuid(),
            tripId: tripId,
            kind: EventKind.DriverPositionUpdated,
            actorId: driver.Id,
            location: factory.CreatePoint(new Coordinate(lng, lat)),
            timestamp: ts,
            payloadJson: JsonSerializer.Serialize(new
            {
                driverId = driver.Id,
                lat,
                lng,
            })), Context.ConnectionAborted).ConfigureAwait(false);
        await _events.SaveChangesAsync(Context.ConnectionAborted).ConfigureAwait(false);

        await Clients.Group(GroupName(tripId))
            .DriverPositionUpdated(driver.Id, lat, lng, ts.UtcDateTime)
            .ConfigureAwait(false);

        await _etaQueue.EnqueueAsync(
            new EtaRecomputeJob(tripId, driver.Id, lat, lng, ts),
            Context.ConnectionAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// Passenger confirms they have arrived at the rendezvous stop. Persists a
    /// <see cref="EventKind.PassengerAtStop"/> row and broadcasts to the trip's group so the
    /// driver sees the confirmation. The client identifies itself via
    /// <paramref name="participantId"/>.
    /// </summary>
    public async Task PassengerCheckInAsync(Guid tripId, Guid participantId, Guid stopId)
    {
        var trip = await _trips.LookupAsync(tripId, Context.ConnectionAborted).ConfigureAwait(false);
        if (trip is null)
        {
            throw new HubException("Trip not found.");
        }

        var participantList = await _participants.ListForTripAsync(tripId, Context.ConnectionAborted).ConfigureAwait(false);
        var participant = participantList.FirstOrDefault(p => p.Id == participantId);
        if (participant is null)
        {
            throw new HubException("Participant not found on this trip.");
        }

        var ts = _clock.UtcNow;
        await _events.AddAsync(new TripEvent(
            id: Guid.NewGuid(),
            tripId: tripId,
            kind: EventKind.PassengerAtStop,
            actorId: participant.Id,
            location: participant.Home,
            timestamp: ts,
            payloadJson: JsonSerializer.Serialize(new
            {
                participantId = participant.Id,
                stopId = stopId.ToString(),
            })), Context.ConnectionAborted).ConfigureAwait(false);
        await _events.SaveChangesAsync(Context.ConnectionAborted).ConfigureAwait(false);

        await Clients.Group(GroupName(tripId))
            .PassengerAtStop(participant.Id, stopId, ts.UtcDateTime)
            .ConfigureAwait(false);
    }

    private async Task<Participant?> ResolveDriverAsync(Guid tripId, Guid participantId, CancellationToken ct)
    {
        var roster = await _participants.ListForTripAsync(tripId, ct).ConfigureAwait(false);
        return roster.FirstOrDefault(p => p.Id == participantId && p.HasCar);
    }
}
