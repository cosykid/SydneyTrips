using Trips.Core.Domain;

namespace Trips.Core.Contracts;

/// <summary>
/// API view of one persisted <see cref="TripEvent"/>. Returned by <c>GET /trips/{id}/events</c> so a
/// late-joining hub client can rebuild state before subscribing to live updates.
/// </summary>
public sealed record TripEventDto(
    Guid Id,
    Guid TripId,
    EventKind Kind,
    Guid? ActorId,
    double? Longitude,
    double? Latitude,
    DateTimeOffset Timestamp,
    string? PayloadJson);
