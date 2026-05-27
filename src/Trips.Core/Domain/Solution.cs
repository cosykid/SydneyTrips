using NetTopologySuite.Geometries;

namespace Trips.Core.Domain;

/// <summary>
/// A concrete assignment of passengers to drivers plus the ordered pickup sequence per driver.
/// Belongs to an <see cref="OptimisationRun"/>. A run can hold several Pareto-comparable solutions.
/// </summary>
public sealed class Solution
{
    public Guid Id { get; private set; }
    public Guid OptimisationRunId { get; private set; }
    public string Label { get; private set; }
    public double Objective { get; private set; }

    /// <summary>Per-term breakdown of the objective: drive / stops / PT access / spread / fairness.</summary>
    public double[] ObjectiveTerms { get; private set; }

    private readonly List<DriverRoute> _routes = new();
    public IReadOnlyList<DriverRoute> Routes => _routes;

    private Solution()
    {
        Label = string.Empty;
        ObjectiveTerms = Array.Empty<double>();
    }

    public Solution(
        Guid id,
        Guid optimisationRunId,
        string label,
        double objective,
        IReadOnlyList<double> objectiveTerms,
        IEnumerable<DriverRoute> routes)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(label);
        ArgumentNullException.ThrowIfNull(objectiveTerms);
        ArgumentNullException.ThrowIfNull(routes);

        Id = id;
        OptimisationRunId = optimisationRunId;
        Label = label;
        Objective = objective;
        ObjectiveTerms = objectiveTerms.ToArray();
        _routes.AddRange(routes);
    }
}

/// <summary>
/// One driver's ordered sequence of stops in a <see cref="Solution"/>, ending at the trip destination.
/// </summary>
public sealed class DriverRoute
{
    public Guid Id { get; private set; }
    public Guid SolutionId { get; private set; }
    public Guid DriverId { get; private set; }
    public double TravelMins { get; private set; }
    public int OrderIndex { get; private set; }

    private readonly List<Stop> _stops = new();
    public IReadOnlyList<Stop> Stops => _stops;

    private DriverRoute()
    {
    }

    public DriverRoute(
        Guid id,
        Guid solutionId,
        Guid driverId,
        double travelMins,
        int orderIndex,
        IEnumerable<Stop> stops)
    {
        ArgumentNullException.ThrowIfNull(stops);
        Id = id;
        SolutionId = solutionId;
        DriverId = driverId;
        TravelMins = travelMins;
        OrderIndex = orderIndex;
        _stops.AddRange(stops);
    }
}

/// <summary>
/// One stop on a <see cref="DriverRoute"/>: the location, who is picked up there, and the ETA.
/// </summary>
public sealed class Stop
{
    public Guid Id { get; private set; }
    public Guid DriverRouteId { get; private set; }
    public int OrderIndex { get; private set; }
    public Point Location { get; private set; }
    public Guid CandidateNodeId { get; private set; }
    public DateTimeOffset EstimatedArrival { get; private set; }

    /// <summary>Comma-separated participant IDs picked up at this stop. Modelled as a value-converted list in EF.</summary>
    public IReadOnlyList<Guid> Pickups { get; private set; }

    private Stop()
    {
        Location = default!;
        Pickups = Array.Empty<Guid>();
    }

    public Stop(
        Guid id,
        Guid driverRouteId,
        int orderIndex,
        Point location,
        Guid candidateNodeId,
        DateTimeOffset estimatedArrival,
        IReadOnlyList<Guid> pickups)
    {
        ArgumentNullException.ThrowIfNull(location);
        ArgumentNullException.ThrowIfNull(pickups);

        Id = id;
        DriverRouteId = driverRouteId;
        OrderIndex = orderIndex;
        Location = location;
        CandidateNodeId = candidateNodeId;
        EstimatedArrival = estimatedArrival;
        Pickups = pickups.ToArray();
    }
}
