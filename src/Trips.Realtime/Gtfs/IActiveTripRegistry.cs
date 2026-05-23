using System.Collections.Concurrent;

namespace Trips.Realtime.Gtfs;

/// <summary>
/// Tracks which trips have at least one connected client right now. The GTFS-RT worker uses this
/// to scope its feed polling — without it the worker has no way to know which trips are "active"
/// from the database alone (a trip is live the moment passengers or drivers connect to the hub,
/// not when their <c>DepartAt</c> timestamp matches wall clock).
/// </summary>
public interface IActiveTripRegistry
{
    /// <summary>Record that a connection has joined the given trip.</summary>
    void OnConnectionJoined(Guid tripId, string connectionId);

    /// <summary>Remove a connection. The trip is considered inactive when its connection set empties.</summary>
    void OnConnectionLeft(Guid tripId, string connectionId);

    /// <summary>Trips that currently have at least one connected hub client.</summary>
    IReadOnlyCollection<Guid> GetActiveTrips();
}

/// <summary>
/// In-memory implementation. SignalR with the Redis backplane will see hub connections on the
/// instance they actually connect to — for a multi-instance deployment we'd swap this for a Redis
/// set keyed on <c>trip-active:{tripId}</c>, but the in-memory version is sufficient for the
/// single-instance dev/test target of WS5.
/// </summary>
public sealed class InMemoryActiveTripRegistry : IActiveTripRegistry
{
    private readonly ConcurrentDictionary<Guid, ConcurrentDictionary<string, byte>> _trips = new();

    public void OnConnectionJoined(Guid tripId, string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        var set = _trips.GetOrAdd(tripId, _ => new ConcurrentDictionary<string, byte>());
        set[connectionId] = 0;
    }

    public void OnConnectionLeft(Guid tripId, string connectionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(connectionId);
        if (_trips.TryGetValue(tripId, out var set))
        {
            set.TryRemove(connectionId, out _);
            if (set.IsEmpty)
            {
                _trips.TryRemove(tripId, out _);
            }
        }
    }

    public IReadOnlyCollection<Guid> GetActiveTrips() => _trips.Keys.ToList();
}
