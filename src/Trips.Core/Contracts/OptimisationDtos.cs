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
    IReadOnlyList<StopDto> Stops,
    /// <summary>Estimated clock time the driver reaches the destination, on the same timeline as each
    /// <see cref="StopDto.EstimatedArrival"/>. Anchored backwards from the trip's arrival target so the
    /// driver lands a few minutes early instead of leaving at the scheduled <c>DepartAt</c> and idling
    /// (see <see cref="Departure"/>). Null when the solution is mapped without its trip (no window to
    /// anchor on); the FE then omits the destination clock. Computed at map time — not persisted.</summary>
    DateTimeOffset? DestinationArrival = null,
    /// <summary>Estimated clock time the driver leaves home — <c>DestinationArrival − TravelMins</c>.
    /// Rather than depart at the scheduled <c>DepartAt</c> (which is set well before the arrival window
    /// and made drivers arrive far too early), the displayed timeline slides later so the driver leaves
    /// just-in-time. Never earlier than <c>DepartAt</c>: when the window is too tight to reach on time
    /// it pins to <c>DepartAt</c> and the driver arrives as early as the drive allows. Null when mapped
    /// without its trip. Computed at map time — not persisted.</summary>
    DateTimeOffset? Departure = null);

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
/// raw TfNSW mode string ("walk", "train", "metro", "bus", "ferry", "lightrail", "unknown"). The
/// remaining fields drive the planner's Google-Maps-style timed itinerary on hover:
/// <see cref="DurationMins"/> is the leg's travel time; <see cref="FromName"/>/<see cref="ToName"/>
/// are the stop names; <see cref="RouteShortName"/> is the line label; <see cref="DepartureTime"/>/
/// <see cref="ArrivalTime"/> are the scheduled clock times. All default so stub / pre-feature data
/// still serialises — the FE renders whatever's present.</summary>
public sealed record PathLegDto(
    string Mode,
    PathDto Path,
    int DurationMins = 0,
    string? FromName = null,
    string? ToName = null,
    string? RouteShortName = null,
    DateTimeOffset? DepartureTime = null,
    DateTimeOffset? ArrivalTime = null);
