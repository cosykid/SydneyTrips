using Trips.Core.Domain;

namespace Trips.Api.Optimisation;

/// <summary>
/// Item enqueued by endpoints; consumed by <see cref="OptimisationRunner"/>.
/// </summary>
public sealed record OptimisationJob(
    Guid TripId,
    Guid RunId,
    ObjectiveWeights Weights,
    SolverKind Solver,
    bool RepairHint);
