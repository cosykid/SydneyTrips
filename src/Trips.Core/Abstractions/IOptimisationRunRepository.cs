using Trips.Core.Domain;

namespace Trips.Core.Abstractions;

/// <summary>
/// Repository for optimisation runs. Hot path is the background worker writing status updates
/// and the API polling for the best solution.
/// </summary>
public interface IOptimisationRunRepository
{
    Task<OptimisationRun?> GetByIdAsync(Guid runId, CancellationToken ct);

    /// <summary>Load a run together with all its solutions, driver routes, and stops.</summary>
    Task<OptimisationRun?> GetWithSolutionsAsync(Guid runId, CancellationToken ct);

    Task<IReadOnlyList<OptimisationRun>> ListForTripAsync(Guid tripId, CancellationToken ct);

    Task AddAsync(OptimisationRun run, CancellationToken ct);

    Task<int> SaveChangesAsync(CancellationToken ct);
}
