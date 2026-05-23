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
    DateTimeOffset EstimatedArrival,
    IReadOnlyList<Guid> Pickups);
