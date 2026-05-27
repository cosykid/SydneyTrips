using Trips.Core.Abstractions;
using Trips.Core.Domain;
using Trips.Optimisation.Common;
using Trips.Optimisation.Tests.Helpers;

namespace Trips.Optimisation.Tests;

/// <summary>
/// Pure-evaluator tests: build a tiny plan by hand, ensure <see cref="ObjectiveEvaluator.Evaluate"/>
/// computes the right per-term values. These tests don't invoke OR-Tools or the heuristic — they
/// pin the *definition* of the objective so that solver changes can't silently shift it.
/// </summary>
public class ObjectiveEvaluatorTests
{
    [Fact]
    public void Travel_Stops_Walk_SumCorrectly()
    {
        // 1 driver, 2 passengers, 2 candidates each.
        // Nodes: 0=d0_origin, 1=c0(p0), 2=c1(p0)/c2(p1), 3=c3(p1), 4=destination
        // Plan: visit c0 (pickup p0), then c1/c2 (pickup p1 at c2 which equals c1).
        var matrix = InstanceBuilder.SymmetricMatrix(5, 0,
            (0, 1, 10),
            (1, 2, 5),
            (2, 4, 8),
            (0, 2, 13),
            (1, 4, 11));
        var input = InstanceBuilder.Build(
            driverCount: 1,
            driverSeats: new[] { 2 },
            passengerCandidatesLocal: new[]
            {
                new[] { 0, 1 },  // p0 -> c0 (node 1), c1 (node 2)
                new[] { 1, 2 },  // p1 -> c1 (node 2), c2 (node 3)
            },
            passengerWalks: new[]
            {
                new[] { 2, 5 },
                new[] { 3, 7 },
            },
            candidateCount: 3,
            matrix: matrix,
            weights: new ObjectiveWeights(DriveTime: 1.0, StopCount: 1.0, WalkAndPt: 1.0, ArrivalSpread: 0.0, Fairness: 0.0));

        // Driver visits node 1 then node 2 then destination (node 4)
        var routesPerDriver = new IReadOnlyList<int>[] { new[] { 1, 2 } };
        var nodeChoice = new[] { 1, 2 };       // p0 at node 1 (c0), p1 at node 2 (c1)
        var driverChoice = new[] { 0, 0 };

        var eval = ObjectiveEvaluator.Evaluate(input, routesPerDriver, nodeChoice, driverChoice, destinationNodeIndex: 4);

        // Travel = (10 + 5 + 8) = 23 raw min, × DriveTime weight 1.0 × DriverMinutePremium.
        var travelTerm = 23.0 * ObjectiveEvaluator.DriverMinutePremium;
        Assert.Equal(travelTerm, eval.Terms[0], 5);
        // Stops = 2 * StopCost (1.0) * 1.0 = 2.0
        Assert.Equal(2.0, eval.Terms[1], 5);
        // Walk = 2 (p0 at c0) + 3 (p1 at c1) = 5
        Assert.Equal(5.0, eval.Terms[2], 5);
        Assert.Equal(0.0, eval.Terms[3], 5);
        Assert.Equal(0.0, eval.Terms[4], 5);
        Assert.Equal(travelTerm + 2.0 + 5.0, eval.Objective, 5);
    }

    [Fact]
    public void Fairness_EqualsMaxExtraPickupBurden()
    {
        // 2 drivers each carrying 1 passenger. d0's route is 20 min but its direct solo trip is
        // 15, so its pickup burden is 5. d1's route equals its direct solo trip, so its burden is 0.
        // Fairness tracks the maximum extra pickup burden, not raw geography-driven drive time.
        // Nodes: 0=d0_origin, 1=d1_origin, 2=c0(p0), 3=c1(p1), 4=destination.
        var matrix = InstanceBuilder.SymmetricMatrix(5, 0,
            (0, 2, 10), (2, 4, 10),   // d0 → 20 min
            (0, 4, 15),               // d0 solo → 15 min, burden 5
            (1, 3, 2), (3, 4, 3),     // d1 → 5 min
            (1, 4, 5));               // d1 solo → 5 min, burden 0
        var input = InstanceBuilder.Build(
            driverCount: 2,
            driverSeats: new[] { 2, 2 },
            passengerCandidatesLocal: new[] { new[] { 0 }, new[] { 1 } },
            passengerWalks: new[] { new[] { 0 }, new[] { 0 } },
            candidateCount: 2,
            matrix: matrix,
            weights: new ObjectiveWeights(DriveTime: 0.0, StopCount: 0.0, WalkAndPt: 0.0, ArrivalSpread: 0.0, Fairness: 1.0));

        var eval = ObjectiveEvaluator.Evaluate(
            input,
            new IReadOnlyList<int>[] { new[] { 2 }, new[] { 3 } },
            new[] { 2, 3 },
            new[] { 0, 1 },
            destinationNodeIndex: 4);

        Assert.Equal(5.0, eval.Terms[4], 5);
        Assert.Equal(5.0, eval.Objective, 5);
    }

    [Fact]
    public void Fairness_DoesNotRewardExtendingShortRoutes()
    {
        // The critical regression guard: under min-max burden, lengthening the shorter driver's
        // route must not lower the fairness term. We compare two plans that have the same maximum
        // burden but different shorter-driver route times — fairness stays put.
        //
        // Both plans: d0 = 20 min route against a 10 min solo baseline, so burden = 10. Plan A:
        // d1 = 5 min route against a 5 min baseline, burden = 0. Plan B: d1 = 15 min route against
        // the same baseline, burden = 10. A spread surrogate would say Plan B is "better";
        // min-max burden says they're tied, so the solver has no reason to inflate d1.
        // Nodes: 0=d0_origin, 1=d1_origin, 2=p0_at_d0, 3=p1_short, 4=p1_long, 5=destination.
        var matrix = InstanceBuilder.SymmetricMatrix(6, 0,
            (0, 2, 10), (2, 5, 10),   // d0 → 20 min (Plan A and B)
            (0, 5, 10),               // d0 solo → 10 min
            (1, 3, 2), (3, 5, 3),     // d1 short → 5 min (Plan A)
            (1, 5, 5),                // d1 solo → 5 min
            (1, 4, 7), (4, 5, 8));    // d1 long  → 15 min (Plan B)
        var input = InstanceBuilder.Build(
            driverCount: 2,
            driverSeats: new[] { 2, 2 },
            passengerCandidatesLocal: new[] { new[] { 0 }, new[] { 1, 2 } },
            passengerWalks: new[] { new[] { 0 }, new[] { 0, 0 } },
            candidateCount: 3,
            matrix: matrix,
            weights: new ObjectiveWeights(DriveTime: 0.0, StopCount: 0.0, WalkAndPt: 0.0, ArrivalSpread: 0.0, Fairness: 1.0));

        var planA = ObjectiveEvaluator.Evaluate(
            input,
            new IReadOnlyList<int>[] { new[] { 2 }, new[] { 3 } },
            new[] { 2, 3 },
            new[] { 0, 1 },
            destinationNodeIndex: 5);
        var planB = ObjectiveEvaluator.Evaluate(
            input,
            new IReadOnlyList<int>[] { new[] { 2 }, new[] { 4 } },
            new[] { 2, 4 },
            new[] { 0, 1 },
            destinationNodeIndex: 5);

        Assert.Equal(10.0, planA.Terms[4], 5);
        Assert.Equal(10.0, planB.Terms[4], 5);
    }

    [Fact]
    public void Fairness_ChargesOnlyActivePickupBurden_NotIdleSoloDrive()
    {
        // d0 active: 20 min route against a 15 min solo baseline, so burden = 5. d1 is idle with a
        // 30 min solo drive, but that is not pickup burden and must not become the fairness max.
        // Nodes: 0=d0_origin, 1=d1_origin, 2=c0, 3=destination.
        var matrix = InstanceBuilder.SymmetricMatrix(4, 0,
            (0, 2, 10), (2, 3, 10),   // d0 → 20 min
            (0, 3, 15),               // d0 solo → 15 min
            (1, 3, 30));              // d1 idle solo drive, ignored by fairness
        var input = InstanceBuilder.Build(
            driverCount: 2,
            driverSeats: new[] { 2, 2 },
            passengerCandidatesLocal: new[] { new[] { 0 }, new[] { 0 } },
            passengerWalks: new[] { new[] { 0 }, new[] { 0 } },
            candidateCount: 1,
            matrix: matrix,
            weights: new ObjectiveWeights(DriveTime: 0.0, StopCount: 0.0, WalkAndPt: 0.0, ArrivalSpread: 0.0, Fairness: 1.0));

        var eval = ObjectiveEvaluator.Evaluate(
            input,
            new IReadOnlyList<int>[] { new[] { 2 }, Array.Empty<int>() },
            new[] { 2, 2 },
            new[] { 0, 0 },
            destinationNodeIndex: 3);

        Assert.Equal(5.0, eval.Terms[4], 5);
    }

    [Fact]
    public void FairnessZero_DoesNotLeakIdleSoloDriveIntoTravelTerm()
    {
        // Nodes: 0=d0_origin, 1=d1_origin, 2=c0, 3=destination.
        var matrix = InstanceBuilder.SymmetricMatrix(4, 0,
            (0, 2, 10), (2, 3, 10),   // d0 → 20 min
            (1, 3, 30));              // d1's solo drive home→dest = 30 min

        // The DriveTime term still counts only active driver travel: d1's idle solo drive is display
        // information, not solver travel.
        //   travel term = DriveTime(1.0) × premium(2.0) × 20 = 40.
        var input = InstanceBuilder.Build(
            driverCount: 2,
            driverSeats: new[] { 2, 2 },
            passengerCandidatesLocal: new[] { new[] { 0 }, new[] { 0 } },
            passengerWalks: new[] { new[] { 0 }, new[] { 0 } },
            candidateCount: 1,
            matrix: matrix,
            weights: new ObjectiveWeights(DriveTime: 1.0, StopCount: 0.0, WalkAndPt: 0.0, ArrivalSpread: 0.0, Fairness: 0.0));
        var eval = ObjectiveEvaluator.Evaluate(
            input,
            new IReadOnlyList<int>[] { new[] { 2 }, Array.Empty<int>() },
            new[] { 2, 2 },
            new[] { 0, 0 },
            destinationNodeIndex: 3);
        Assert.Equal(0.0, eval.Terms[4], 5);                                   // fairness term off
        Assert.Equal(40.0, eval.Terms[0], 5);                                  // idle solo not in travel
    }

    [Fact]
    public void TwoDrivers_SpreadEqualsMaxMinusMinArrival()
    {
        // 2 drivers each carrying 1 passenger. Driver 0 arrives at minute 20, driver 1 at minute 5.
        // Nodes: 0=d0_origin, 1=d1_origin, 2=c0(p0), 3=c1(p1), 4=destination
        var matrix = InstanceBuilder.SymmetricMatrix(5, 0,
            (0, 2, 10),
            (2, 4, 10),  // d0 takes 20 mins
            (1, 3, 2),
            (3, 4, 3));  // d1 takes 5 mins
        var input = InstanceBuilder.Build(
            driverCount: 2,
            driverSeats: new[] { 2, 2 },
            passengerCandidatesLocal: new[]
            {
                new[] { 0 },
                new[] { 1 },
            },
            passengerWalks: new[]
            {
                new[] { 0 },
                new[] { 0 },
            },
            candidateCount: 2,
            matrix: matrix,
            weights: new ObjectiveWeights(DriveTime: 0.0, StopCount: 0.0, WalkAndPt: 0.0, ArrivalSpread: 1.0, Fairness: 0.0));

        var routesPerDriver = new IReadOnlyList<int>[] { new[] { 2 }, new[] { 3 } };
        var nodeChoice = new[] { 2, 3 };
        var driverChoice = new[] { 0, 1 };

        var eval = ObjectiveEvaluator.Evaluate(input, routesPerDriver, nodeChoice, driverChoice, destinationNodeIndex: 4);
        // Spread = 20 - 5 = 15
        Assert.Equal(15.0, eval.Terms[3], 5);
        Assert.Equal(15.0, eval.Objective, 5);
    }
}
