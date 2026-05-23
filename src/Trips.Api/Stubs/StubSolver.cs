using Trips.Core.Abstractions;
using Trips.Core.Domain;

namespace Trips.Api.Stubs;

/// <summary>
/// Stand-in <see cref="ISolver"/> registered only when WS3 isn't merged in. Produces a trivial
/// canned solution so end-to-end paths (and integration tests) work without the optimisation core.
/// Real implementations from <c>Trips.Optimisation</c> replace this via the conditional DI gate.
/// </summary>
internal sealed class StubSolver : ISolver
{
    public SolverKind Kind => SolverKind.Heuristic;

    public Task<Solution> SolveAsync(SolverInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);

        // One DriverRoute per driver, no stops. Objective is 0 — tests assert the row exists,
        // not that it is optimal.
        var routes = input.Drivers.Select((driver, index) =>
            new DriverRoute(
                id: Guid.NewGuid(),
                solutionId: Guid.Empty,
                driverId: driver.ParticipantId,
                travelMins: 0,
                orderIndex: index,
                stops: Array.Empty<Stop>())).ToArray();

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
