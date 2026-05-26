using Trips.Core.Domain;

namespace Trips.Core.Contracts;

/// <summary>Weights passed in <see cref="OptimiseRequest"/>. Mirrors <see cref="ObjectiveWeights"/>.</summary>
public sealed record ObjectiveWeightsDto(
    double DriveTime,
    double StopCount,
    double WalkAndPt,
    double ArrivalSpread,
    double Fairness);

/// <summary>Payload to POST /trips/{id}/optimise. Server returns 202 + a run id.</summary>
public sealed record OptimiseRequest(ObjectiveWeightsDto Weights, SolverKind Solver = SolverKind.OrTools);

/// <summary>API view of an optimisation run.</summary>
public sealed record OptimisationRunDto(
    Guid Id,
    Guid TripId,
    OptimisationStatus Status,
    SolverKind Solver,
    ObjectiveWeightsDto Weights,
    DateTimeOffset StartedAt,
    DateTimeOffset? CompletedAt,
    string? FailureReason,
    Guid? BestSolutionId,
    OptimisationStatsDto? Stats);

public sealed record OptimisationStatsDto(
    TimeSpan WallClock,
    int IterationsOrNodes,
    double BestObjective,
    double? LpRelaxation,
    SolverKind Solver);

/// <summary>API view of a single solution belonging to a run.</summary>
public sealed record SolutionDto(
    Guid Id,
    Guid OptimisationRunId,
    string Label,
    double Objective,
    IReadOnlyList<double> ObjectiveTerms,
    IReadOnlyList<DriverRouteDto> Routes);

public sealed record DriverRouteDto(
    Guid Id,
    Guid DriverId,
    double TravelMins,
    int OrderIndex,
    IReadOnlyList<StopDto> Stops);

public sealed record StopDto(
    Guid Id,
    int OrderIndex,
    double Longitude,
    double Latitude,
    Guid CandidateNodeId,
    /// <summary>Kind of the candidate node served at this stop — lets the FE pick a
    /// transit-modality icon (train/bus/ferry/light-rail) for transit hubs.</summary>
    CandidateNodeKindDto NodeKind,
    DateTimeOffset EstimatedArrival,
    /// <summary>Per-passenger pickup detail — replaces the bare guid list with the walk/PT split
    /// of each passenger's home → pickup journey, so the planner can render walking vs public
    /// transport legs without re-joining against the participant's candidate-node set.</summary>
    IReadOnlyList<PickupLegDto> Pickups);

/// <summary>One passenger's leg from home to this stop. <see cref="WalkMins"/> is the walking
/// portion; <see cref="PtMins"/> is the public-transport portion (bus/train/ferry/light rail). The
/// total home-to-pickup time is the sum. <see cref="Path"/> is <em>this passenger's</em> real
/// home→pickup geometry — carried per-leg (not just on the stop's single canonical candidate node)
/// so that when several passengers share a pickup hub each renders along their own route rather
/// than a straight crow-fly line.</summary>
public sealed record PickupLegDto(
    Guid ParticipantId,
    int WalkMins,
    int PtMins,
    PathDto? Path,
    /// <summary>The same home→pickup journey as <see cref="Path"/>, split into mode-tagged
    /// segments so the map can colour each leg (walk / train / bus / ferry / light rail)
    /// distinctly. Null when the backend has no per-leg geometry (stub / pre-feature data);
    /// the FE falls back to the single-colour <see cref="Path"/>.</summary>
    IReadOnlyList<PathLegDto>? PathLegs = null);

/// <summary>One mode-tagged segment of a passenger's home→pickup journey. <see cref="Mode"/> is the
/// raw TfNSW mode string ("walk", "train", "metro", "bus", "ferry", "lightrail", "unknown").</summary>
public sealed record PathLegDto(string Mode, PathDto Path);
