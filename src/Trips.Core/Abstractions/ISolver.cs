using Trips.Core.Domain;

namespace Trips.Core.Abstractions;

/// <summary>
/// Contract implemented by every solver — OR-Tools, heuristic, future variants. Solvers operate
/// on a precomputed travel-time matrix (see <see cref="SolverInput"/>) so they never hit live APIs.
/// </summary>
public interface ISolver
{
    /// <summary>Identifies which solver this is — used in logs, run records, and the benchmark report.</summary>
    SolverKind Kind { get; }

    /// <summary>Solve the flexible-pickup DARP. Returns the best <see cref="Solution"/> found within the cancellation budget.</summary>
    Task<Solution> SolveAsync(SolverInput input, CancellationToken ct);
}

/// <summary>
/// Self-contained input to a solver run. Everything needed to evaluate the objective lives here so
/// solvers don't need a database or HTTP clients during the inner loop.
/// </summary>
/// <param name="RunId">Owning <see cref="OptimisationRun"/> id; embedded in the produced <see cref="Solution"/>.</param>
/// <param name="TripId">Owning trip id.</param>
/// <param name="Weights">Objective weights for this run.</param>
/// <param name="Drivers">Drivers available: id + seats.</param>
/// <param name="Passengers">Passengers needing a ride: id + each one's feasible candidate nodes.</param>
/// <param name="Nodes">All candidate nodes referenced by passengers, plus driver origins and the destination.</param>
/// <param name="TravelMatrix">Driving-time matrix in minutes between every node pair, indexed by <see cref="SolverNode.Index"/>.</param>
/// <param name="DepartAt">Anchor time for arrival/spread calculations.</param>
public sealed record SolverInput(
    Guid RunId,
    Guid TripId,
    ObjectiveWeights Weights,
    IReadOnlyList<SolverDriver> Drivers,
    IReadOnlyList<SolverPassenger> Passengers,
    IReadOnlyList<SolverNode> Nodes,
    double[,] TravelMatrix,
    DateTimeOffset DepartAt);

/// <summary>One driver entry in <see cref="SolverInput"/>.</summary>
/// <param name="ParticipantId">Domain participant id.</param>
/// <param name="OriginNodeIndex">Index of the driver's starting node in <see cref="SolverInput.Nodes"/>.</param>
/// <param name="Seats">Passenger capacity.</param>
public sealed record SolverDriver(Guid ParticipantId, int OriginNodeIndex, int Seats);

/// <summary>One passenger entry in <see cref="SolverInput"/>.</summary>
/// <param name="ParticipantId">Domain participant id.</param>
/// <param name="CandidateNodeIndices">Indices into <see cref="SolverInput.Nodes"/> of feasible pickup nodes for this passenger.</param>
/// <param name="WalkPtMinsByNodeIndex">
/// Personal cost (walk + PT minutes) of being picked up at each node index. Same length and ordering as
/// <see cref="CandidateNodeIndices"/>.
/// </param>
public sealed record SolverPassenger(
    Guid ParticipantId,
    IReadOnlyList<int> CandidateNodeIndices,
    IReadOnlyList<int> WalkPtMinsByNodeIndex);

/// <summary>One node (pickup point, driver origin, or destination) flattened into the solver's node table.</summary>
/// <param name="Index">Position in <see cref="SolverInput.Nodes"/>; also the index into <see cref="SolverInput.TravelMatrix"/>.</param>
/// <param name="Kind">Node kind for downstream presentation; the solver doesn't care.</param>
/// <param name="CandidateNodeId">
/// Domain <see cref="Trips.Core.Domain.CandidateNode"/> id, when this row corresponds to one.
/// Null for driver-origin and destination rows.
/// </param>
public sealed record SolverNode(int Index, NodeKind Kind, Guid? CandidateNodeId);
