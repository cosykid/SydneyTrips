using NetTopologySuite.Geometries;

namespace Trips.Core.Domain;

/// <summary>
/// Append-only event log per <see cref="Trip"/>. Used for late-join replay, driver/passenger
/// live coordination, and post-hoc auditing of what happened during a trip.
/// </summary>
public sealed class TripEvent
{
    public Guid Id { get; private set; }
    public Guid TripId { get; private set; }
    public EventKind Kind { get; private set; }
    public Guid? ActorId { get; private set; }
    public Point? Location { get; private set; }
    public DateTimeOffset Timestamp { get; private set; }
    public string? PayloadJson { get; private set; }

    private TripEvent()
    {
    }

    public TripEvent(
        Guid id,
        Guid tripId,
        EventKind kind,
        Guid? actorId,
        Point? location,
        DateTimeOffset timestamp,
        string? payloadJson = null)
    {
        Id = id;
        TripId = tripId;
        Kind = kind;
        ActorId = actorId;
        Location = location;
        Timestamp = timestamp;
        PayloadJson = payloadJson;
    }
}
