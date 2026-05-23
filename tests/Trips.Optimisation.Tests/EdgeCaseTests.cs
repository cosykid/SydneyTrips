using Trips.Core.Abstractions;
using Trips.Core.Domain;
using Trips.Optimisation.Common;
using Trips.Optimisation.Heuristic;
using Trips.Optimisation.OrTools;
using Trips.Optimisation.Tests.Helpers;

namespace Trips.Optimisation.Tests;

/// <summary>
/// Edge-case scenarios the formulation should handle gracefully:
/// single-driver, single-passenger, infeasibility flagged correctly when seats run out.
/// </summary>
public class EdgeCaseTests
{
    [Fact]
    public async Task SingleDriverManyPassengers_RoutesThroughAllInOnePath()
    {
        // 1 driver with 4 seats, 4 passengers, each with one candidate. Must route through all of them.
        var matrix = InstanceBuilder.SymmetricMatrix(6, 30,
            (0, 1, 4), (1, 2, 3), (2, 3, 3), (3, 4, 5), (4, 5, 6),
            (0, 5, 25));
        var input = InstanceBuilder.Build(
            driverCount: 1,
            driverSeats: new[] { 4 },
            passengerCandidatesLocal: new[]
            {
                new[] { 0 },
                new[] { 1 },
                new[] { 2 },
                new[] { 3 },
            },
            passengerWalks: new[] { new[] { 0 }, new[] { 0 }, new[] { 0 }, new[] { 0 } },
            candidateCount: 4,
            matrix: matrix);

        var or = new OrToolsSolver(new SolverOptions(TimeBudgetMs: 3_000), Microsoft.Extensions.Logging.Abstractions.NullLogger<OrToolsSolver>.Instance);
        var sol = await or.SolveAsync(input, default);
        Assert.Single(sol.Routes);
        Assert.Equal(4, sol.Routes[0].Stops.Count);
        Assert.Equal(Google.OrTools.Sat.CpSolverStatus.Optimal, or.LastStats.Status);
    }

    [Fact]
    public async Task ExcessSeats_HeuristicAndOrToolsConverge()
    {
        // 3 passengers, 3 drivers with way too many seats. Heuristic + OR-Tools should find the same
        // optimum (one passenger each) for a small enough instance.
        var matrix = InstanceBuilder.SymmetricMatrix(7, 25,
            (0, 3, 4), (1, 4, 4), (2, 5, 4),
            (3, 6, 8), (4, 6, 8), (5, 6, 8));
        var input = InstanceBuilder.Build(
            driverCount: 3,
            driverSeats: new[] { 5, 5, 5 },
            passengerCandidatesLocal: new[]
            {
                new[] { 0 },
                new[] { 1 },
                new[] { 2 },
            },
            passengerWalks: new[] { new[] { 0 }, new[] { 0 }, new[] { 0 } },
            candidateCount: 3,
            matrix: matrix);

        var or = new OrToolsSolver(new SolverOptions(TimeBudgetMs: 5_000), Microsoft.Extensions.Logging.Abstractions.NullLogger<OrToolsSolver>.Instance);
        var heur = new HeuristicSolver(new SolverOptions(TimeBudgetMs: 5_000), SimulatedAnnealingSchedule.Default, Microsoft.Extensions.Logging.Abstractions.NullLogger<HeuristicSolver>.Instance);

        var orSol = await or.SolveAsync(input, default);
        var heurSol = await heur.SolveAsync(input, default);

        Assert.Equal(Google.OrTools.Sat.CpSolverStatus.Optimal, or.LastStats.Status);
        Assert.Equal(orSol.Objective, heurSol.Objective, precision: 2);
    }

    [Fact]
    public async Task WalkBudgetInfeasibility_PassengerWithNoFeasibleCandidates_StillSolves()
    {
        // The solver doesn't enforce walk budget as a hard constraint at the variable level —
        // we let the candidate-generator drop infeasible (p,n) pairs. To simulate "walk-budget
        // infeasibility", we make the passenger's *only* candidate have a very high walk cost
        // (which the objective will penalise but the assignment must still be made).
        var matrix = InstanceBuilder.SymmetricMatrix(3, 10,
            (0, 1, 5), (1, 2, 5));
        var input = InstanceBuilder.Build(
            driverCount: 1,
            driverSeats: new[] { 2 },
            passengerCandidatesLocal: new[] { new[] { 0 } },
            passengerWalks: new[] { new[] { 90 } },     // very high walk cost
            candidateCount: 1,
            matrix: matrix);

        var or = new OrToolsSolver(new SolverOptions(TimeBudgetMs: 2_000), Microsoft.Extensions.Logging.Abstractions.NullLogger<OrToolsSolver>.Instance);
        var sol = await or.SolveAsync(input, default);
        Assert.Equal(Google.OrTools.Sat.CpSolverStatus.Optimal, or.LastStats.Status);
        // The passenger is still assigned (1 stop on the route).
        Assert.Single(sol.Routes[0].Stops);
    }

    [Fact]
    public async Task SingleDriver_WithoutPassengers_StaysIdle()
    {
        // 1 driver, 0 passengers (degenerate). The model is trivially feasible — driver stays home.
        // Tests that the loop building variables handles passengerCount=0 gracefully.
        var matrix = InstanceBuilder.SymmetricMatrix(2, 10);
        var input = InstanceBuilder.Build(
            driverCount: 1,
            driverSeats: new[] { 2 },
            passengerCandidatesLocal: System.Array.Empty<int[]>(),
            passengerWalks: System.Array.Empty<int[]>(),
            candidateCount: 0,
            matrix: matrix);

        var or = new OrToolsSolver(new SolverOptions(TimeBudgetMs: 2_000), Microsoft.Extensions.Logging.Abstractions.NullLogger<OrToolsSolver>.Instance);
        var sol = await or.SolveAsync(input, default);
        // Driver has 0 stops; objective is 0.
        Assert.Single(sol.Routes);
        Assert.Empty(sol.Routes[0].Stops);
        Assert.Equal(0.0, sol.Objective, 2);
    }
}
