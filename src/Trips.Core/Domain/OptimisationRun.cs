namespace Trips.Core.Domain;

/// <summary>
/// A single invocation of the solver for a <see cref="Trip"/>. Holds the chosen weights,
/// outcome status, and (if successful) the best <see cref="Solution"/> plus any Pareto alternatives.
/// </summary>
public sealed class OptimisationRun
{
    public Guid Id { get; private set; }
    public Guid TripId { get; private set; }
    public OptimisationStatus Status { get; private set; }
    public SolverKind Solver { get; private set; }

    // ObjectiveWeights flattened.
    public double WeightDriveTime { get; private set; }
    public double WeightStopCount { get; private set; }
    public double WeightWalkAndPt { get; private set; }
    public double WeightArrivalSpread { get; private set; }
    public double WeightFairness { get; private set; }

    public DateTimeOffset StartedAt { get; private set; }
    public DateTimeOffset? CompletedAt { get; private set; }
    public string? FailureReason { get; private set; }

    // Stats.
    public TimeSpan? WallClock { get; private set; }
    public int? IterationsOrNodes { get; private set; }
    public double? BestObjective { get; private set; }
    public double? LpRelaxation { get; private set; }

    public Guid? BestSolutionId { get; private set; }

    private readonly List<Solution> _solutions = new();
    public IReadOnlyList<Solution> Solutions => _solutions;

    private OptimisationRun()
    {
    }

    public OptimisationRun(
        Guid id,
        Guid tripId,
        ObjectiveWeights weights,
        SolverKind solver,
        DateTimeOffset startedAt)
    {
        ArgumentNullException.ThrowIfNull(weights);
        Id = id;
        TripId = tripId;
        Solver = solver;
        WeightDriveTime = weights.DriveTime;
        WeightStopCount = weights.StopCount;
        WeightWalkAndPt = weights.WalkAndPt;
        WeightArrivalSpread = weights.ArrivalSpread;
        WeightFairness = weights.Fairness;
        StartedAt = startedAt;
        Status = OptimisationStatus.Pending;
    }

    public ObjectiveWeights Weights => new(
        WeightDriveTime,
        WeightStopCount,
        WeightWalkAndPt,
        WeightArrivalSpread,
        WeightFairness);

    public void MarkRunning() => Status = OptimisationStatus.Running;

    public void MarkCompleted(Solution best, IEnumerable<Solution>? paretoAlternatives, OptimisationStats stats, DateTimeOffset completedAt)
    {
        ArgumentNullException.ThrowIfNull(best);
        ArgumentNullException.ThrowIfNull(stats);

        _solutions.Add(best);
        if (paretoAlternatives is not null)
        {
            _solutions.AddRange(paretoAlternatives);
        }

        BestSolutionId = best.Id;
        WallClock = stats.WallClock;
        IterationsOrNodes = stats.IterationsOrNodes;
        BestObjective = stats.BestObjective;
        LpRelaxation = stats.LpRelaxation;
        Status = OptimisationStatus.Completed;
        CompletedAt = completedAt;
    }

    public void MarkFailed(string reason, DateTimeOffset completedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reason);
        Status = OptimisationStatus.Failed;
        FailureReason = reason;
        CompletedAt = completedAt;
    }

    public void MarkCancelled(DateTimeOffset completedAt)
    {
        Status = OptimisationStatus.Cancelled;
        CompletedAt = completedAt;
    }
}
