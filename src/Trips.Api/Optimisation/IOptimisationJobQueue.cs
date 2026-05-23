using Trips.Core.Domain;

namespace Trips.Api.Optimisation;

/// <summary>
/// Exposed to endpoints — they call <see cref="EnqueueAsync"/> after persisting the run row.
/// The <see cref="OptimisationRunner"/> hosted service consumes the same channel.
/// </summary>
public interface IOptimisationJobQueue
{
    ValueTask EnqueueAsync(
        Guid tripId,
        Guid runId,
        ObjectiveWeights weights,
        SolverKind solver,
        bool repairHint,
        CancellationToken ct);
}
