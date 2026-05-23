using NetTopologySuite.Geometries;
using Trips.Core.Abstractions;
using Trips.Core.Domain;

namespace Trips.Api.Stubs;

/// <summary>
/// Stand-in <see cref="ISolver"/> registered only when WS3 isn't merged in. Produces a trivial
/// canned solution so end-to-end paths (and integration tests) work without the optimisation core.
/// Real implementations from <c>Trips.Optimisation</c> replace this via the conditional DI gate.
///
/// <para>The first driver picks up every passenger at a synthesised single stop located at lat/lng
/// (-33.87, 151.21) — Sydney CBD — so cost-split, calendar, and what-if tests have actual
/// pickup data to operate on. Subsequent drivers (if any) produce empty routes.</para>
/// </summary>
internal sealed class StubSolver : ISolver
{
    public SolverKind Kind => SolverKind.Heuristic;

    public Task<Solution> SolveAsync(SolverInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        var routes = new List<DriverRoute>(input.Drivers.Count);
        for (var i = 0; i < input.Drivers.Count; i++)
        {
            var driver = input.Drivers[i];
            var routeId = Guid.NewGuid();
            var stops = new List<Stop>();
            // First driver carries everyone; their stop aggregates every passenger pickup.
            if (i == 0 && input.Passengers.Count > 0)
            {
                var location = new Point(151.21, -33.87) { SRID = 4326 };
                var pickupIds = input.Passengers.Select(p => p.ParticipantId).ToList();
                stops.Add(new Stop(
                    id: Guid.NewGuid(),
                    driverRouteId: routeId,
                    orderIndex: 0,
                    location: location,
                    candidateNodeId: input.Nodes
                        .FirstOrDefault(n => n.CandidateNodeId is not null && n.CandidateNodeId != Guid.Empty)
                        ?.CandidateNodeId ?? Guid.Empty,
                    estimatedArrival: input.DepartAt,
                    pickups: pickupIds));
            }
            routes.Add(new DriverRoute(
                id: routeId,
                solutionId: Guid.Empty,
                driverId: driver.ParticipantId,
                travelMins: 0,
                orderIndex: i,
                stops: stops));
        }

        var solution = new Solution(
            id: Guid.NewGuid(),
            optimisationRunId: input.RunId,
            label: "stub",
            objective: 0.0,
            objectiveTerms: new[] { 0.0, 0.0, 0.0, 0.0, 0.0 },
            routes: routes);

        return Task.FromResult(solution);
    }
}
