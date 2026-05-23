using Trips.Core.Domain;

namespace Trips.Core.Abstractions;

/// <summary>
/// Repository for the <see cref="Trip"/> aggregate. Intentionally narrow — only the queries
/// the API and background services actually need.
/// </summary>
public interface ITripRepository
{
    Task<Trip?> GetByIdAsync(Guid tripId, CancellationToken ct);

    /// <summary>Load a trip with all its participants and their candidate nodes.</summary>
    Task<Trip?> GetWithParticipantsAsync(Guid tripId, CancellationToken ct);

    /// <summary>Load a trip with its run history (no solutions eagerly loaded).</summary>
    Task<Trip?> GetWithRunsAsync(Guid tripId, CancellationToken ct);

    Task<IReadOnlyList<Trip>> ListForOwnerAsync(Guid ownerId, CancellationToken ct);

    Task AddAsync(Trip trip, CancellationToken ct);

    void Remove(Trip trip);

    Task<int> SaveChangesAsync(CancellationToken ct);
}
