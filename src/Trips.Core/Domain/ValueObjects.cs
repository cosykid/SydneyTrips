using NetTopologySuite.Geometries;

namespace Trips.Core.Domain;

/// <summary>
/// Destination of the trip — a named point in WGS84.
/// </summary>
public sealed record Destination(string Name, Point Location);

/// <summary>
/// Acceptable arrival window at the destination. The solver penalises arrivals
/// outside [<see cref="Earliest"/>, <see cref="Latest"/>] proportional to the deviation.
/// </summary>
public sealed record ArrivalWindow(DateTimeOffset Earliest, DateTimeOffset Latest)
{
    public TimeSpan Span => Latest - Earliest;
}

/// <summary>
/// Per-participant preferences feeding the objective function.
/// </summary>
/// <param name="WalkBudgetMins">Hard ceiling on walk-to-pickup minutes.</param>
/// <param name="DetourToleranceMins">
/// Soft tolerance for being driven past one's destination as part of a multi-pickup route.
/// </param>
/// <param name="FairnessWeight">
/// Personal contribution to the cross-passenger fairness penalty. Higher means the
/// participant is more sensitive to having "the worst" individual outcome.
/// </param>
public sealed record Preferences(
    int WalkBudgetMins,
    int DetourToleranceMins,
    double FairnessWeight)
{
    public static Preferences Default { get; } = new(WalkBudgetMins: 12, DetourToleranceMins: 10, FairnessWeight: 1.0);
}

/// <summary>
/// Multi-objective weights for the solver. All weights are non-negative; relative magnitudes
/// determine the trade-off. Three canonical presets are exposed for the Pareto carousel.
/// </summary>
/// <param name="DriveTime">Weight on total driver minutes.</param>
/// <param name="StopCount">Weight on number of pickup stops (each visit incurs a fixed cost).</param>
/// <param name="WalkAndPt">Weight on cumulative passenger public-transport access minutes,
/// including walking segments.</param>
/// <param name="ArrivalSpread">Weight on spread (max-min) of passenger arrival times at the destination.</param>
/// <param name="Fairness">Weight on driver-load fairness — the maximum extra pickup burden over each
/// driver's direct solo trip, so cranking it pushes the solver to share carpool detours rather than
/// penalise someone for living farther from the destination.</param>
public sealed record ObjectiveWeights(
    double DriveTime,
    double StopCount,
    double WalkAndPt,
    double ArrivalSpread,
    double Fairness)
{
    public static ObjectiveWeights Fastest { get; } = new(DriveTime: 1.0, StopCount: 0.2, WalkAndPt: 0.4, ArrivalSpread: 0.3, Fairness: 0.2);
    public static ObjectiveWeights FewestStops { get; } = new(DriveTime: 0.5, StopCount: 1.0, WalkAndPt: 0.4, ArrivalSpread: 0.2, Fairness: 0.2);
    public static ObjectiveWeights LeastTransit { get; } = new(DriveTime: 0.5, StopCount: 0.2, WalkAndPt: 1.0, ArrivalSpread: 0.2, Fairness: 0.3);
    public static ObjectiveWeights LeastWalking => LeastTransit;

    public static ObjectiveWeights Balanced { get; } = new(DriveTime: 0.6, StopCount: 0.4, WalkAndPt: 0.6, ArrivalSpread: 0.3, Fairness: 0.3);
}

/// <summary>
/// Aggregated runtime stats for an <see cref="OptimisationRun"/>.
/// </summary>
public sealed record OptimisationStats(
    TimeSpan WallClock,
    int IterationsOrNodes,
    double BestObjective,
    double? LpRelaxation,
    SolverKind Solver);
