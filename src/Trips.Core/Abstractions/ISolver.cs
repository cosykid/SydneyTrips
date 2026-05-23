using NetTopologySuite.Geometries;
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
/// <param name="WarmStartHint">Optional warm-start hint from a previous solution (used by what-if re-optimisation).
/// When non-null, OR-Tools applies it via <c>model.AddHint</c> to bias the search toward the locked plan, so
/// minor changes (drop/add passenger) produce minimal-disruption re-optimisation. Null on a cold-start run.</param>
public sealed record SolverInput(
    Guid RunId,
    Guid TripId,
    ObjectiveWeights Weights,
    IReadOnlyList<SolverDriver> Drivers,
    IReadOnlyList<SolverPassenger> Passengers,
    IReadOnlyList<SolverNode> Nodes,
    double[,] TravelMatrix,
    DateTimeOffset DepartAt,
    WarmStartHint? WarmStartHint = null);

/// <summary>
/// Hint that biases an OR-Tools solve toward a previously-locked plan. Encodes the assignment as
/// (passenger → driver, node) tuples and the per-driver ordered node sequence. The solver feeds these
/// to <c>CpModel.AddHint</c> on <c>assign</c>, <c>visit</c>, and <c>arc</c> variables; CP-SAT treats the
/// hint as an initial solution to repair rather than a constraint, so the optimum can still drift.
/// </summary>
/// <param name="Assignments">Passenger → (driver index, node index) pairs from the locked solution.
/// Use <see cref="SolverInput.Drivers"/> / <see cref="SolverInput.Nodes"/> indices, not domain GUIDs.</param>
/// <param name="DriverSequences">For each driver index, the ordered list of pickup-node indices visited.
/// Same coordinate system as <paramref name="Assignments"/>.</param>
public sealed record WarmStartHint(
    IReadOnlyList<WarmStartAssignment> Assignments,
    IReadOnlyList<IReadOnlyList<int>> DriverSequences);

/// <summary>One passenger's pinned location in a <see cref="WarmStartHint"/>.</summary>
/// <param name="PassengerIndex">Index into <see cref="SolverInput.Passengers"/>.</param>
/// <param name="DriverIndex">Index into <see cref="SolverInput.Drivers"/>.</param>
/// <param name="NodeIndex">Node index from <see cref="SolverInput.Nodes"/>.</param>
public sealed record WarmStartAssignment(int PassengerIndex, int DriverIndex, int NodeIndex);

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
/// <param name="Location">
/// WGS84 point for this node (driver home, candidate pickup, or destination). Stamped onto the
/// produced <see cref="Stop.Location"/> by <c>SolutionBuilder</c>, so it must be the real geometry —
/// the solver itself ignores it (it works purely off <see cref="SolverInput.TravelMatrix"/>).
/// </param>
public sealed record SolverNode(int Index, NodeKind Kind, Guid? CandidateNodeId, Point Location);
