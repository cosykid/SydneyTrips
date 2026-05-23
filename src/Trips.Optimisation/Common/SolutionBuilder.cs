using NetTopologySuite.Geometries;
using Trips.Core.Abstractions;
using Trips.Core.Domain;

namespace Trips.Optimisation.Common;

/// <summary>
/// Turns an internal solver-flavoured plan (driver→sequence of node indices) into the persistent
/// <see cref="Solution"/> domain object. The two solvers share this so route ordering, ETA stamping,
/// and per-stop pickup aggregation are identical regardless of who produced the plan.
/// </summary>
public static class SolutionBuilder
{
    /// <summary>
    /// Construct a <see cref="Solution"/> from a fully-resolved plan.
    /// </summary>
    /// <param name="input">Solver input — supplies the trip/run ids, drivers, passengers, matrix.</param>
    /// <param name="label">Human-friendly label (e.g. "Fastest", "Fewest stops"). Required.</param>
    /// <param name="routesPerDriver">For each driver, the ordered list of node indices visited.</param>
    /// <param name="nodeChoicePerPassenger">For each passenger, the chosen node index.</param>
    /// <param name="driverPerPassenger">For each passenger, the assigned driver index.</param>
    /// <param name="destinationNodeIndex">Index of the destination node.</param>
    /// <param name="nodeLocations">Per-node WGS84 point — used to stamp <see cref="Stop.Location"/>.</param>
    public static Solution Build(
        SolverInput input,
        string label,
        IReadOnlyList<IReadOnlyList<int>> routesPerDriver,
        IReadOnlyList<int> nodeChoicePerPassenger,
        IReadOnlyList<int> driverPerPassenger,
        int destinationNodeIndex,
        IReadOnlyList<Point> nodeLocations)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentException.ThrowIfNullOrWhiteSpace(label);

        var eval = ObjectiveEvaluator.Evaluate(
            input,
            routesPerDriver,
            nodeChoicePerPassenger,
            driverPerPassenger,
            destinationNodeIndex);

        var routes = new List<DriverRoute>(input.Drivers.Count);
        for (var d = 0; d < input.Drivers.Count; d++)
        {
            var driver = input.Drivers[d];
            var seq = routesPerDriver[d];
            var stops = new List<Stop>(seq.Count);
            var routeId = Guid.NewGuid();

            // Bucket passengers per node so multiple passengers picked up at the same node share one Stop.
            var pickupsByNode = new Dictionary<int, List<Guid>>();
            for (var p = 0; p < input.Passengers.Count; p++)
            {
                if (driverPerPassenger[p] != d) continue;
                var node = nodeChoicePerPassenger[p];
                if (!pickupsByNode.TryGetValue(node, out var list))
                {
                    list = new List<Guid>();
                    pickupsByNode[node] = list;
                }
                list.Add(input.Passengers[p].ParticipantId);
            }

            // Build stops in visit order; tick over arrival time per leg.
            var cursorMins = 0.0;
            var prev = driver.OriginNodeIndex;
            for (var i = 0; i < seq.Count; i++)
            {
                var node = seq[i];
                cursorMins += input.TravelMatrix[prev, node];
                var nodeInfo = input.Nodes[node];
                var pickups = pickupsByNode.TryGetValue(node, out var list) ? list : new List<Guid>();
                var stop = new Stop(
                    id: Guid.NewGuid(),
                    driverRouteId: routeId,
                    orderIndex: i,
                    location: nodeLocations[node],
                    candidateNodeId: nodeInfo.CandidateNodeId ?? Guid.Empty,
                    estimatedArrival: input.DepartAt.AddMinutes(cursorMins),
                    pickups: pickups);
                stops.Add(stop);
                prev = node;
            }
            cursorMins += input.TravelMatrix[prev, destinationNodeIndex];

            routes.Add(new DriverRoute(
                id: routeId,
                solutionId: Guid.Empty,
                driverId: driver.ParticipantId,
                travelMins: eval.DriverTravelMins[d],
                orderIndex: d,
                stops: stops));
        }

        return new Solution(
            id: Guid.NewGuid(),
            optimisationRunId: input.RunId,
            label: label,
            objective: eval.Objective,
            objectiveTerms: eval.Terms,
            routes: routes);
    }
}
