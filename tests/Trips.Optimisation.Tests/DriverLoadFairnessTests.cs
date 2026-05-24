using Trips.Core.Abstractions;
using Trips.Core.Domain;
using Trips.Optimisation.Common;
using Trips.Optimisation.OrTools;
using Trips.Optimisation.Tests.Helpers;

namespace Trips.Optimisation.Tests;

/// <summary>
/// Locks in the new "Fair sharing" semantics on the OR-Tools solver: the Fairness weight now
/// penalises driver-load spread (maxLoad − minLoad over drivers), not cross-passenger journey
/// spread. The old formulation was identically zero whenever every passenger rode the same
/// driver, so the user-facing slider did nothing on trips with one capacious driver — the
/// solver consolidated all pickups into a single car regardless of where the slider sat.
/// </summary>
public class DriverLoadFairnessTests
{
    [Fact]
    public async Task FairnessHigh_DistributesPassengersAcrossDrivers_WhenCapacityAllows()
    {
        // 2 drivers (each with 4 seats — easily enough for all 4 passengers in one car),
        // 4 passengers each with a single nearby candidate node (so the only meaningful choice
        // the solver makes is which driver picks them up).
        //
        // Nodes: 0=d0, 1=d1, 2=c0(p0), 3=c1(p1), 4=c2(p2), 5=c3(p3), 6=dest.
        // Matrix: arrange so d0 is roughly equidistant to all candidates and so is d1; the
        // travel-time minimum is "one driver does it all" (avoids the second driver's overhead).
        var matrix = InstanceBuilder.SymmetricMatrix(7, 50,
            // both drivers similarly close to every candidate
            (0, 2, 8), (0, 3, 9), (0, 4, 10), (0, 5, 11),
            (1, 2, 9), (1, 3, 8), (1, 4, 11), (1, 5, 10),
            // candidate ↔ destination
            (2, 6, 12), (3, 6, 12), (4, 6, 14), (5, 6, 14),
            // candidate ↔ candidate so within-route stitching is cheap
            (2, 3, 4), (3, 4, 4), (4, 5, 4), (2, 4, 7), (3, 5, 7), (2, 5, 9)
        );

        var passengerCandidates = new[]
        {
            new[] { 0 }, new[] { 1 }, new[] { 2 }, new[] { 3 },
        };
        var passengerWalks = new[]
        {
            new[] { 1 }, new[] { 1 }, new[] { 1 }, new[] { 1 },
        };

        // Crank Fairness; keep DriveTime modest so the solver isn't tempted to swallow load
        // imbalance for the sake of a few saved driver-minutes.
        var weights = new ObjectiveWeights(
            DriveTime: 0.2,
            StopCount: 0.1,
            WalkAndPt: 0.1,
            ArrivalSpread: 0.1,
            Fairness: 1.0);

        var input = InstanceBuilder.Build(
            driverCount: 2,
            driverSeats: new[] { 4, 4 },
            passengerCandidatesLocal: passengerCandidates,
            passengerWalks: passengerWalks,
            candidateCount: 4,
            matrix: matrix,
            weights: weights);

        var solver = new OrToolsSolver(
            new SolverOptions(TimeBudgetMs: 15_000),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OrToolsSolver>.Instance);
        var solution = await solver.SolveAsync(input, default);

        // Count assignments per driver — the new Fairness term should produce a 2/2 split (or
        // 3/1 at worst) rather than 4/0.
        var perDriver = solution.Routes
            .ToDictionary(r => r.DriverId, r => r.Stops.SelectMany(s => s.Pickups).Count());
        var loads = perDriver.Values.OrderByDescending(n => n).ToList();
        var status = solver.LastStats.Status;
        Assert.True(loads[0] - loads[loads.Count - 1] <= 2,
            $"expected driver loads to be roughly balanced; got [{string.Join(", ", loads)}] " +
            $"with status={status}, objective={solution.Objective}");
    }

    [Fact]
    public async Task FairnessZero_StillConsolidates_WhenItSavesDriveTime()
    {
        // Same shape but with Fairness=0: the solver should keep its old consolidation
        // behaviour (drive-time minimisation dominates).
        var matrix = InstanceBuilder.SymmetricMatrix(7, 50,
            (0, 2, 8), (0, 3, 9), (0, 4, 10), (0, 5, 11),
            (1, 2, 9), (1, 3, 8), (1, 4, 11), (1, 5, 10),
            (2, 6, 12), (3, 6, 12), (4, 6, 14), (5, 6, 14),
            (2, 3, 4), (3, 4, 4), (4, 5, 4), (2, 4, 7), (3, 5, 7), (2, 5, 9)
        );

        var weights = new ObjectiveWeights(
            DriveTime: 1.0,
            StopCount: 0.2,
            WalkAndPt: 0.2,
            ArrivalSpread: 0.1,
            Fairness: 0.0);

        var input = InstanceBuilder.Build(
            driverCount: 2,
            driverSeats: new[] { 4, 4 },
            passengerCandidatesLocal: new[]
            {
                new[] { 0 }, new[] { 1 }, new[] { 2 }, new[] { 3 },
            },
            passengerWalks: new[]
            {
                new[] { 1 }, new[] { 1 }, new[] { 1 }, new[] { 1 },
            },
            candidateCount: 4,
            matrix: matrix,
            weights: weights);

        var solver = new OrToolsSolver(
            new SolverOptions(TimeBudgetMs: 5_000),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OrToolsSolver>.Instance);
        var solution = await solver.SolveAsync(input, default);

        // With Fairness=0 we expect consolidation: at most one driver carries everyone.
        var loadedDrivers = solution.Routes.Count(r => r.Stops.SelectMany(s => s.Pickups).Any());
        Assert.True(loadedDrivers <= 1,
            $"expected consolidation when fairness=0; got {loadedDrivers} loaded drivers");
    }
}
