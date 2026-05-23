using System.Globalization;
using System.Security.Claims;
using System.Text.Json;
using Microsoft.AspNetCore.Authorization;
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
/// fan out to exactly the right set of clients. Group membership is gated on the trip's
/// owner/participant authorisation check (via <see cref="ITripHubAuthorizer"/>) — clients can't
/// observe a trip they aren't part of.
/// </summary>
[Authorize]
public sealed class TripHub : Hub<ITripHubClient>
{
    private readonly ITripHubAuthorizer _authorizer;
    private readonly ITripEventRepository _events;
    private readonly IParticipantRepository _participants;
    private readonly EtaRecomputeQueue _etaQueue;
    private readonly IActiveTripRegistry _activeTrips;
    private readonly IClock _clock;
    private readonly ILogger<TripHub> _logger;

    public TripHub(
        ITripHubAuthorizer authorizer,
        ITripEventRepository events,
        IParticipantRepository participants,
        EtaRecomputeQueue etaQueue,
        IActiveTripRegistry activeTrips,
        IClock clock,
        ILogger<TripHub> logger)
    {
        ArgumentNullException.ThrowIfNull(authorizer);
        ArgumentNullException.ThrowIfNull(events);
        ArgumentNullException.ThrowIfNull(participants);
        ArgumentNullException.ThrowIfNull(etaQueue);
        ArgumentNullException.ThrowIfNull(activeTrips);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _authorizer = authorizer;
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
    /// Join the SignalR group for <paramref name="tripId"/>. Throws <see cref="HubException"/> if
    /// the caller isn't owner or participant — surfaces to the client as a rejected invocation.
    /// </summary>
    public async Task JoinTripAsync(Guid tripId)
    {
        var userId = GetUserId();
        if (userId == Guid.Empty)
        {
            throw new HubException("Unauthenticated.");
        }
        var trip = await _authorizer.AuthorizeAsync(tripId, userId, Context.ConnectionAborted).ConfigureAwait(false);
        if (trip is null)
        {
            throw new HubException("Trip not found or you are not a participant.");
        }
        await Groups.AddToGroupAsync(Context.ConnectionId, GroupName(tripId), Context.ConnectionAborted).ConfigureAwait(false);
        _activeTrips.OnConnectionJoined(tripId, Context.ConnectionId);
        _logger.LogInformation("Connection {Conn} (user {User}) joined trip {Trip}", Context.ConnectionId, userId, tripId);
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
    /// Driver client publishes a fresh GPS position. The hub
    /// (a) persists a <see cref="EventKind.DriverPositionUpdated"/> event row,
    /// (b) broadcasts <c>DriverPositionUpdated</c> to the trip's group, and
    /// (c) enqueues an ETA recompute job so passengers see refreshed arrival times.
    /// The caller must be a participant with <see cref="Participant.HasCar"/> = true.
    /// </summary>
    public async Task PublishDriverPositionAsync(Guid tripId, double lat, double lng)
    {
        var userId = GetUserId();
        var trip = await _authorizer.AuthorizeAsync(tripId, userId, Context.ConnectionAborted).ConfigureAwait(false);
        if (trip is null)
        {
            throw new HubException("Trip not found or you are not a participant.");
        }

        var driverParticipant = await ResolveDriverAsync(tripId, userId, Context.ConnectionAborted).ConfigureAwait(false);
        if (driverParticipant is null)
        {
            throw new HubException("Only a participant with a car may publish positions.");
        }

        var ts = _clock.UtcNow;
        var factory = new GeometryFactory(new PrecisionModel(), 4326);

        await _events.AddAsync(new TripEvent(
            id: Guid.NewGuid(),
            tripId: tripId,
            kind: EventKind.DriverPositionUpdated,
            actorId: userId,
            location: factory.CreatePoint(new Coordinate(lng, lat)),
            timestamp: ts,
            payloadJson: JsonSerializer.Serialize(new
            {
                driverId = driverParticipant.Id,
                lat,
                lng,
            })), Context.ConnectionAborted).ConfigureAwait(false);
        await _events.SaveChangesAsync(Context.ConnectionAborted).ConfigureAwait(false);

        await Clients.Group(GroupName(tripId))
            .DriverPositionUpdated(driverParticipant.Id, lat, lng, ts.UtcDateTime)
            .ConfigureAwait(false);

        await _etaQueue.EnqueueAsync(
            new EtaRecomputeJob(tripId, driverParticipant.Id, lat, lng, ts),
            Context.ConnectionAborted).ConfigureAwait(false);
    }

    /// <summary>
    /// Passenger confirms they have arrived at the rendezvous stop. Persists a
    /// <see cref="EventKind.PassengerAtStop"/> row and broadcasts to the trip's group so the
    /// driver sees the confirmation.
    /// </summary>
    public async Task PassengerCheckInAsync(Guid tripId, Guid stopId)
    {
        var userId = GetUserId();
        var trip = await _authorizer.AuthorizeAsync(tripId, userId, Context.ConnectionAborted).ConfigureAwait(false);
        if (trip is null)
        {
            throw new HubException("Trip not found or you are not a participant.");
        }

        var participantList = await _participants.ListForTripAsync(tripId, Context.ConnectionAborted).ConfigureAwait(false);
        var participant = participantList.FirstOrDefault(p => p.UserId == userId);
        if (participant is null)
        {
            throw new HubException("Only a participant may check in.");
        }

        var ts = _clock.UtcNow;
        await _events.AddAsync(new TripEvent(
            id: Guid.NewGuid(),
            tripId: tripId,
            kind: EventKind.PassengerAtStop,
            actorId: userId,
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

    private Guid GetUserId()
    {
        var user = Context.User;
        if (user?.Identity?.IsAuthenticated != true)
        {
            return Guid.Empty;
        }
        // After Jwt bearer validation runs, the `sub` claim is remapped to ClaimTypes.NameIdentifier
        // by default (Microsoft's inbound claim type map). Check both to stay safe across
        // configurations.
        var raw = user.FindFirstValue(ClaimTypes.NameIdentifier)
                  ?? user.FindFirstValue("sub");
        return raw is not null && Guid.TryParse(raw, CultureInfo.InvariantCulture, out var g) ? g : Guid.Empty;
    }

    private async Task<Participant?> ResolveDriverAsync(Guid tripId, Guid userId, CancellationToken ct)
    {
        var roster = await _participants.ListForTripAsync(tripId, ct).ConfigureAwait(false);
        return roster.FirstOrDefault(p => p.UserId == userId && p.HasCar);
    }
}
