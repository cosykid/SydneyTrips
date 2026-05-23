using Trips.Core.Domain;

namespace Trips.Core.Abstractions;

/// <summary>
/// Repository contract for fetching a <see cref="Solution"/> with its routes + stops eagerly loaded.
/// Used by the cost-split service and any caller that needs to walk a locked solution's structure.
/// </summary>
public interface ISolutionRepository
{
    /// <summary>Load a solution by id with its <see cref="Solution.Routes"/> and stops included.</summary>
    Task<Solution?> GetByIdAsync(Guid solutionId, CancellationToken ct);
}
