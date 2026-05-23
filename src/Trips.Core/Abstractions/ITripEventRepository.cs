using Trips.Core.Domain;

namespace Trips.Core.Abstractions;

/// <summary>
/// Append-only event log per trip. Used by SignalR coordination and late-join replay.
/// </summary>
public interface ITripEventRepository
{
    Task AddAsync(TripEvent evt, CancellationToken ct);

    Task<IReadOnlyList<TripEvent>> ListForTripAsync(Guid tripId, CancellationToken ct);

    /// <summary>
    /// Events for a trip with timestamp >= <paramref name="since"/>. Used by clients
    /// re-syncing after a brief disconnect from the hub.
    /// </summary>
    Task<IReadOnlyList<TripEvent>> ListSinceAsync(Guid tripId, DateTimeOffset since, CancellationToken ct);

    Task<int> SaveChangesAsync(CancellationToken ct);
}
