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

        // Travel = 10 + 5 + 8 = 23 (× weight 1.0)
        Assert.Equal(23.0, eval.Terms[0], 5);
        // Stops = 2 * StopCost (1.0) * 1.0 = 2.0
        Assert.Equal(2.0, eval.Terms[1], 5);
        // Walk = 2 (p0 at c0) + 3 (p1 at c1) = 5
        Assert.Equal(5.0, eval.Terms[2], 5);
        Assert.Equal(0.0, eval.Terms[3], 5);
        Assert.Equal(0.0, eval.Terms[4], 5);
        Assert.Equal(30.0, eval.Objective, 5);
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
