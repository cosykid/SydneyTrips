using Trips.Core.Domain;

namespace Trips.Core.Abstractions;

/// <summary>
/// Captures everything needed by the what-if service to re-optimise from a locked solution:
/// the persisted <see cref="Solution"/> plus the <see cref="SolverInput"/> that produced it.
///
/// <para>The <see cref="SolverInput"/> is not persisted directly (it's a derived value of the trip +
/// run). The data layer rebuilds it from the trip's participants and the run's weights — see
/// <c>Trips.Data.Repositories.LockedContextRepository</c>.</para>
/// </summary>
public sealed record LockedContext(Solution Solution, SolverInput Input);

/// <summary>
/// Repository contract for fetching a <see cref="LockedContext"/>. Implementations live in
/// <c>Trips.Data</c>; the optimisation layer (and the API endpoint layer) depend on this interface.
/// </summary>
public interface ILockedContextRepository
{
    Task<LockedContext?> GetByIdAsync(Guid lockedSolutionId, CancellationToken ct);
}
