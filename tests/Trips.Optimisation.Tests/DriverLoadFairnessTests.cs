using Trips.Core.Abstractions;
using Trips.Core.Domain;
using Trips.Optimisation.Common;
using Trips.Optimisation.OrTools;
using Trips.Optimisation.Tests.Helpers;

namespace Trips.Optimisation.Tests;

/// <summary>
/// Locks in the Fairness surrogate's behaviour on the OR-Tools solver. Fairness is now max extra
/// pickup burden above each driver's direct solo trip — see <see cref="ObjectiveEvaluator.Evaluate"/>
/// for the full history of why we discarded the previous spread/range/raw-time/count formulations.
/// The tests below pin the properties that motivated the switch:
///
/// <list type="number">
///   <item><description>When activating a second car genuinely lowers the longest driver's clock,
///   cranking Fairness must drive the split — even if pure DriveTime prefers consolidation.</description></item>
///   <item><description>When activating a second car doesn't help (no useful pickup for it, or its
///   route would be longer than the single-driver tour), Fairness must not force the split. The
///   old spread surrogate's bug was that it would inflate the *shorter* driver's route to "balance"
///   the gap, which produced visibly absurd detours in the UI.</description></item>
/// </list>
/// </summary>
public class DriverLoadFairnessTests
{
    [Fact]
    public async Task FairnessHigh_DistributesPassengers_WhenItLowersMaxDriverTime()
    {
        // Two passenger clusters with a costly hop between them. Both drivers sit at the head of
        // a cluster, so each can serve "their" cluster cheaply. Consolidation forces one driver to
        // make the expensive between-cluster hop; splitting avoids it.
        //
        // Nodes: 0=d0, 1=d1, 2=c0, 3=c1, 4=c2, 5=c3, 6=dest.
        // Cluster A = {c0, c1} near d0. Cluster B = {c2, c3} near d1.
        var matrix = InstanceBuilder.SymmetricMatrix(7, 50,
            // driver → own-cluster candidates (cheap)
            (0, 2, 4), (0, 3, 4), (1, 4, 4), (1, 5, 4),
            // driver → other-cluster candidates (expensive)
            (0, 4, 14), (0, 5, 14), (1, 2, 14), (1, 3, 14),
            // intra-cluster candidate hops (cheap)
            (2, 3, 2), (4, 5, 2),
            // inter-cluster candidate hops (expensive — this is what consolidation has to pay)
            (2, 4, 10), (2, 5, 10), (3, 4, 10), (3, 5, 10),
            // dest equidistant from every candidate
            (2, 6, 12), (3, 6, 12), (4, 6, 12), (5, 6, 12),
            // dest from driver origins (only relevant if a driver carries no one)
            (0, 6, 20), (1, 6, 20)
        );

        // Pure-DriveTime view (no Fairness, premium ×2 baked into ObjectiveEvaluator):
        //   Consolidation (d0 does all): 4+2+10+2+12 = 30  →  drive-cost = 30 × 0.2 × 2 = 12
        //   Split 2/2:                   18 + 18    = 36  →  drive-cost = 36 × 0.2 × 2 = 14.4
        // DriveTime alone prefers consolidation by 2.4. Fairness with the extra-burden surrogate
        // saves 10 minutes of max burden by splitting: consolidated d0 is 30 against a 20 minute
        // solo baseline, while each split route is below the same solo baseline.
        var weights = new ObjectiveWeights(
            DriveTime: 0.2,
            StopCount: 0.1,
            WalkAndPt: 0.1,
            ArrivalSpread: 0.1,
            Fairness: 1.0);

        var input = InstanceBuilder.Build(
            driverCount: 2,
            driverSeats: new[] { 4, 4 },
            passengerCandidatesLocal: new[] { new[] { 0 }, new[] { 1 }, new[] { 2 }, new[] { 3 } },
            passengerWalks: new[] { new[] { 1 }, new[] { 1 }, new[] { 1 }, new[] { 1 } },
            candidateCount: 4,
            matrix: matrix,
            weights: weights);

        var solver = new OrToolsSolver(
            new SolverOptions(TimeBudgetMs: 15_000),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OrToolsSolver>.Instance);
        var solution = await solver.SolveAsync(input, default);

        var perDriver = solution.Routes
            .ToDictionary(r => r.DriverId, r => r.Stops.SelectMany(s => s.Pickups).Count());
        var loads = perDriver.Values.OrderByDescending(n => n).ToList();
        var status = solver.LastStats.Status;
        Assert.True(loads[0] - loads[loads.Count - 1] <= 2,
            $"expected the Fairness slider to split the cluster pickups across drivers; " +
            $"got [{string.Join(", ", loads)}] with status={status}, objective={solution.Objective}");
    }

    [Fact]
    public async Task FairnessHigh_DoesNotInflateShortRoutes_WhenSplittingWouldnt()
    {
        // The regression guard motivated by the map screenshot the user flagged: jack (close to
        // destination) was being sent on an absurd detour just to "balance" sangmin's long route
        // under the old spread surrogate. Here d0 sits near every candidate and the destination;
        // d1 is parked far away and can't help. Activating d1 would make d1's route LONGER than
        // d0's consolidated tour, so min-max correctly leaves d1 idle. (Under spread, the solver
        // would either still split — inflating d1 — or, worse, force d0 to detour to inflate the
        // shorter route. Neither happens here.)
        //
        // Nodes: 0=d0 (close), 1=d1 (far), 2=c0, 3=c1, 4=c2, 5=c3, 6=dest.
        var matrix = InstanceBuilder.SymmetricMatrix(7, 50,
            // d0 close to every candidate; d1 far from every candidate
            (0, 2, 3), (0, 3, 4), (0, 4, 5), (0, 5, 6),
            (1, 2, 25), (1, 3, 25), (1, 4, 25), (1, 5, 25),
            // candidates clustered tightly
            (2, 3, 2), (2, 4, 4), (2, 5, 6), (3, 4, 2), (3, 5, 4), (4, 5, 2),
            // dest equidistant from candidates; reachable from both drivers
            (2, 6, 10), (3, 6, 10), (4, 6, 10), (5, 6, 10),
            (0, 6, 15), (1, 6, 15)
        );

        // Consolidated (d0 does all): 3+2+2+2+10 = 19 against a 15 minute solo baseline, so burden
        // is only 4. d1 stays idle → burden 0. Any split puts d1 onto a 25+...+10 route (≥ 37 min),
        // a 22+ minute burden. Min-max burden correctly leaves d1 idle even at Fairness=1.0;
        // pre-fix spread/raw-time surrogates could inflate either d0 or d1 for the wrong reason.
        var weights = new ObjectiveWeights(
            DriveTime: 0.2,
            StopCount: 0.1,
            WalkAndPt: 0.1,
            ArrivalSpread: 0.1,
            Fairness: 1.0);

        var input = InstanceBuilder.Build(
            driverCount: 2,
            driverSeats: new[] { 4, 4 },
            passengerCandidatesLocal: new[] { new[] { 0 }, new[] { 1 }, new[] { 2 }, new[] { 3 } },
            passengerWalks: new[] { new[] { 1 }, new[] { 1 }, new[] { 1 }, new[] { 1 } },
            candidateCount: 4,
            matrix: matrix,
            weights: weights);

        var solver = new OrToolsSolver(
            new SolverOptions(TimeBudgetMs: 15_000),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OrToolsSolver>.Instance);
        var solution = await solver.SolveAsync(input, default);

        // The well-placed driver picks up everyone; the far driver stays idle.
        var loadedDrivers = solution.Routes.Count(r => r.Stops.SelectMany(s => s.Pickups).Any());
        Assert.True(loadedDrivers <= 1,
            $"expected min-max Fairness to leave the far driver idle when activating it would " +
            $"raise the longest driver's clock; got {loadedDrivers} loaded drivers, " +
            $"objective={solution.Objective}");
    }

    [Fact]
    public async Task FairnessZero_Consolidates_WhenItSavesDriveTime()
    {
        // Same shape as the cluster-split case above, but with Fairness=0: pure DriveTime then
        // prefers consolidation onto a single driver because the inter-cluster hop is cheaper
        // than spinning up a second car's "home → first pickup" + extra "last → dest" overhead.
        // Pins the property that Fairness=0 returns the solver to consolidation behaviour.
        var matrix = InstanceBuilder.SymmetricMatrix(7, 50,
            (0, 2, 3), (0, 3, 4), (0, 4, 5), (0, 5, 6),
            (1, 2, 25), (1, 3, 25), (1, 4, 25), (1, 5, 25),
            (2, 3, 2), (2, 4, 4), (2, 5, 6), (3, 4, 2), (3, 5, 4), (4, 5, 2),
            (2, 6, 10), (3, 6, 10), (4, 6, 10), (5, 6, 10),
            (0, 6, 15), (1, 6, 15)
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
            passengerCandidatesLocal: new[] { new[] { 0 }, new[] { 1 }, new[] { 2 }, new[] { 3 } },
            passengerWalks: new[] { new[] { 1 }, new[] { 1 }, new[] { 1 }, new[] { 1 } },
            candidateCount: 4,
            matrix: matrix,
            weights: weights);

        var solver = new OrToolsSolver(
            new SolverOptions(TimeBudgetMs: 5_000),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<OrToolsSolver>.Instance);
        var solution = await solver.SolveAsync(input, default);

        var loadedDrivers = solution.Routes.Count(r => r.Stops.SelectMany(s => s.Pickups).Any());
        Assert.True(loadedDrivers <= 1,
            $"expected consolidation when fairness=0; got {loadedDrivers} loaded drivers");
    }
}
