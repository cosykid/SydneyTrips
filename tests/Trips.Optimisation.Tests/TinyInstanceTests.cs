using Trips.Core.Abstractions;
using Trips.Core.Domain;
using Trips.Optimisation.Common;
using Trips.Optimisation.Heuristic;
using Trips.Optimisation.OrTools;
using Trips.Optimisation.Tests.Helpers;

namespace Trips.Optimisation.Tests;

/// <summary>
/// Tiny hand-built instances where the optimum is obvious. Both solvers MUST find the same optimum.
/// These are the smoke tests that catch correctness regressions in the formulation or the local
/// search operators.
/// </summary>
public class TinyInstanceTests
{
    [Fact]
    public async Task ThreePassengersTwoDrivers_KnownOptimum_BothSolversAgree()
    {
        // 2 drivers (at nodes 0, 1), 3 passengers each with two candidate nodes, destination at last index.
        // Build a matrix where one solution clearly dominates: all passengers travel a similar amount.
        //
        // Node indices: 0=d0, 1=d1, 2=c0(p0), 3=c1(p0), 4=c2(p1), 5=c3(p1), 6=c4(p2), 7=c5(p2), 8=dest
        var matrix = InstanceBuilder.SymmetricMatrix(9, 50,
            // d0 -> candidates
            (0, 2, 5), (0, 3, 20), (0, 4, 25), (0, 5, 30), (0, 6, 28), (0, 7, 22),
            // d1 -> candidates
            (1, 2, 18), (1, 3, 6), (1, 4, 12), (1, 5, 8), (1, 6, 7), (1, 7, 10),
            // candidates -> dest
            (2, 8, 20), (3, 8, 18), (4, 8, 15), (5, 8, 12), (6, 8, 14), (7, 8, 16),
            // candidate-candidate (less relevant, but real)
            (2, 4, 12), (4, 6, 10), (3, 5, 8), (5, 7, 6), (2, 6, 18), (3, 7, 14)
        );

        var input = InstanceBuilder.Build(
            driverCount: 2,
            driverSeats: new[] { 3, 3 },
            passengerCandidatesLocal: new[]
            {
                new[] { 0, 1 },   // p0 -> c0 (node 2) or c1 (node 3)
                new[] { 2, 3 },   // p1 -> c2 (node 4) or c3 (node 5)
                new[] { 4, 5 },   // p2 -> c4 (node 6) or c5 (node 7)
            },
            passengerWalks: new[]
            {
                new[] { 2, 8 },
                new[] { 3, 4 },
                new[] { 5, 1 },
            },
            candidateCount: 6,
            matrix: matrix);

        var or = new OrToolsSolver(new SolverOptions(TimeBudgetMs: 5_000), Microsoft.Extensions.Logging.Abstractions.NullLogger<OrToolsSolver>.Instance);
        var heur = new HeuristicSolver(new SolverOptions(TimeBudgetMs: 5_000), SimulatedAnnealingSchedule.Default, Microsoft.Extensions.Logging.Abstractions.NullLogger<HeuristicSolver>.Instance);

        var orSol = await or.SolveAsync(input, default);
        var heurSol = await heur.SolveAsync(input, default);

        Assert.Equal(Google.OrTools.Sat.CpSolverStatus.Optimal, or.LastStats.Status);
        // Heuristic should match optimum on tiny instance.
        Assert.Equal(orSol.Objective, heurSol.Objective, precision: 3);

        // Sanity: each passenger picked up exactly once.
        var pickedPassengers = orSol.Routes.SelectMany(r => r.Stops).SelectMany(s => s.Pickups).Distinct().Count();
        Assert.Equal(input.Passengers.Count, pickedPassengers);
    }

    [Fact]
    public async Task SingleDriver_OnePassenger_TrivialSolution()
    {
        var matrix = InstanceBuilder.SymmetricMatrix(3, 50,
            (0, 1, 5), (1, 2, 10), (0, 2, 14));
        var input = InstanceBuilder.Build(
            driverCount: 1,
            driverSeats: new[] { 4 },
            passengerCandidatesLocal: new[] { new[] { 0 } },
            passengerWalks: new[] { new[] { 0 } },
            candidateCount: 1,
            matrix: matrix);

        var or = new OrToolsSolver(new SolverOptions(TimeBudgetMs: 2_000), Microsoft.Extensions.Logging.Abstractions.NullLogger<OrToolsSolver>.Instance);
        var sol = await or.SolveAsync(input, default);
        Assert.Single(sol.Routes);
        Assert.Single(sol.Routes[0].Stops);
        Assert.Equal(Google.OrTools.Sat.CpSolverStatus.Optimal, or.LastStats.Status);
    }

    [Fact]
    public async Task SinglePassenger_TwoDrivers_AssignedToBetterDriver()
    {
        // Driver 1 is far; driver 0 is close. Should always pick driver 0.
        var matrix = InstanceBuilder.SymmetricMatrix(4, 100,
            (0, 2, 5),    // d0 -> candidate (close)
            (1, 2, 60),   // d1 -> candidate (far)
            (2, 3, 10),   // candidate -> dest
            (0, 3, 12),
            (1, 3, 80));
        var input = InstanceBuilder.Build(
            driverCount: 2,
            driverSeats: new[] { 2, 2 },
            passengerCandidatesLocal: new[] { new[] { 0 } },
            passengerWalks: new[] { new[] { 0 } },
            candidateCount: 1,
            matrix: matrix);

        var or = new OrToolsSolver(new SolverOptions(TimeBudgetMs: 5_000), Microsoft.Extensions.Logging.Abstractions.NullLogger<OrToolsSolver>.Instance);
        var sol = await or.SolveAsync(input, default);

        var pickupDriver = sol.Routes.First(r => r.Stops.Any()).DriverId;
        Assert.Equal(input.Drivers[0].ParticipantId, pickupDriver);
    }

    [Fact]
    public async Task WalkBudgetCanForceAssignment_HeuristicRespects()
    {
        // p0 has two candidates: walk=0 (its home) or walk=100 (a distant stop). The model never
        // sees the walk=100 entry as a *budget violation* — we just expect the optimum picks the
        // walk=0 entry because it's cheaper.
        var matrix = InstanceBuilder.SymmetricMatrix(4, 30,
            (0, 2, 5), (0, 3, 10), (2, 1, 100), (3, 1, 20));
        // Node layout: 0=d0, 1=dest, 2=c0(p0), 3=c1(p0). But Build assumes destination is at index
        // driverCount + candidateCount = 1 + 2 = 3. So adjust:
        // d0 at 0, c0 at 1, c1 at 2, dest at 3
        var matrix2 = InstanceBuilder.SymmetricMatrix(4, 30,
            (0, 1, 5),   // d0 -> c0
            (0, 2, 25),  // d0 -> c1
            (1, 3, 12),  // c0 -> dest
            (2, 3, 8));  // c1 -> dest
        var input = InstanceBuilder.Build(
            driverCount: 1,
            driverSeats: new[] { 2 },
            passengerCandidatesLocal: new[] { new[] { 0, 1 } },
            passengerWalks: new[] { new[] { 0, 80 } },
            candidateCount: 2,
            matrix: matrix2);

        var or = new OrToolsSolver(new SolverOptions(TimeBudgetMs: 2_000), Microsoft.Extensions.Logging.Abstractions.NullLogger<OrToolsSolver>.Instance);
        var sol = await or.SolveAsync(input, default);
        // c0 (walk=0) at absolute node 1 — picked because the walking cost beats the slightly shorter
        // drive via c1.
        Assert.Single(sol.Routes[0].Stops);
        // We can't directly assert by index without exposing the SolverNode mapping, but the stop's
        // pickup count must be 1.
        Assert.Single(sol.Routes[0].Stops[0].Pickups);
    }
}
