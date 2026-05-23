using Trips.Core.Abstractions;
using Trips.Core.Domain;
using Trips.Optimisation.Common;
using Trips.Optimisation.OrTools;
using Trips.Optimisation.Tests.Helpers;

namespace Trips.Optimisation.Tests;

/// <summary>
/// Constraint correctness tests — each one stresses a specific constraint family of the CP-SAT
/// formulation: capacity, MTZ subtour, walk budget, etc. We rely on the OR-Tools solver to honour
/// these constraints; if the model is wrong, these tests fail.
/// </summary>
public class ConstraintTests
{
    [Fact]
    public async Task Capacity_FewSeatsForcesMultipleDrivers()
    {
        // 4 passengers, 2 drivers with 2 seats each — solution must split 2/2.
        var matrix = InstanceBuilder.SymmetricMatrix(7, 50,
            (0, 2, 5), (0, 3, 5),         // d0 close to c0, c1
            (1, 4, 5), (1, 5, 5),         // d1 close to c2, c3
            (2, 6, 10), (3, 6, 10),
            (4, 6, 10), (5, 6, 10));
        var input = InstanceBuilder.Build(
            driverCount: 2,
            driverSeats: new[] { 2, 2 },
            passengerCandidatesLocal: new[]
            {
                new[] { 0 },  // p0 -> c0
                new[] { 1 },  // p1 -> c1
                new[] { 2 },  // p2 -> c2
                new[] { 3 },  // p3 -> c3
            },
            passengerWalks: new[] { new[] { 0 }, new[] { 0 }, new[] { 0 }, new[] { 0 } },
            candidateCount: 4,
            matrix: matrix);

        var or = new OrToolsSolver(new SolverOptions(TimeBudgetMs: 3_000), Microsoft.Extensions.Logging.Abstractions.NullLogger<OrToolsSolver>.Instance);
        var sol = await or.SolveAsync(input, default);

        foreach (var route in sol.Routes)
        {
            var loadOnRoute = route.Stops.SelectMany(s => s.Pickups).Count();
            Assert.True(loadOnRoute <= 2, $"driver {route.DriverId} carries {loadOnRoute} > 2 seats");
        }
    }

    [Fact]
    public async Task EveryPassengerExactlyOncePickup()
    {
        var matrix = InstanceBuilder.SymmetricMatrix(6, 30,
            (0, 2, 5), (0, 3, 7), (0, 4, 9),
            (1, 2, 6), (1, 3, 4), (1, 4, 12),
            (2, 5, 10), (3, 5, 8), (4, 5, 11));
        var input = InstanceBuilder.Build(
            driverCount: 2,
            driverSeats: new[] { 3, 3 },
            passengerCandidatesLocal: new[]
            {
                new[] { 0 },
                new[] { 1 },
                new[] { 2 },
            },
            passengerWalks: new[] { new[] { 0 }, new[] { 0 }, new[] { 0 } },
            candidateCount: 3,
            matrix: matrix);

        var or = new OrToolsSolver(new SolverOptions(TimeBudgetMs: 3_000), Microsoft.Extensions.Logging.Abstractions.NullLogger<OrToolsSolver>.Instance);
        var sol = await or.SolveAsync(input, default);

        var allPickups = sol.Routes.SelectMany(r => r.Stops).SelectMany(s => s.Pickups).ToList();
        Assert.Equal(input.Passengers.Count, allPickups.Count);
        Assert.Equal(input.Passengers.Count, allPickups.Distinct().Count());
    }

    [Fact]
    public async Task SubtourElimination_NoVisitedNodeAppearsTwiceInOneRoute()
    {
        // If MTZ is working, a route can't visit any pickup node more than once.
        var matrix = InstanceBuilder.SymmetricMatrix(5, 10,
            (0, 1, 3), (0, 2, 3), (1, 2, 2), (1, 4, 8), (2, 4, 8));
        var input = InstanceBuilder.Build(
            driverCount: 1,
            driverSeats: new[] { 3 },
            passengerCandidatesLocal: new[]
            {
                new[] { 0 },
                new[] { 1 },
            },
            passengerWalks: new[] { new[] { 0 }, new[] { 0 } },
            candidateCount: 2,
            matrix: matrix);

        var or = new OrToolsSolver(new SolverOptions(TimeBudgetMs: 2_000), Microsoft.Extensions.Logging.Abstractions.NullLogger<OrToolsSolver>.Instance);
        var sol = await or.SolveAsync(input, default);

        var locations = sol.Routes[0].Stops.Select(s => s.CandidateNodeId).ToList();
        Assert.Equal(locations.Count, locations.Distinct().Count());
    }

    [Fact]
    public async Task ArrivalTimes_MonotonicAlongRoute()
    {
        // Pin a specific ordering by giving driver a constrained route, then verify arrival times
        // on the output Stops are strictly non-decreasing.
        var matrix = InstanceBuilder.SymmetricMatrix(5, 20,
            (0, 1, 6), (1, 2, 4), (2, 3, 3), (3, 4, 5),
            (0, 4, 18), (1, 4, 14));
        var input = InstanceBuilder.Build(
            driverCount: 1,
            driverSeats: new[] { 5 },
            passengerCandidatesLocal: new[]
            {
                new[] { 0 },
                new[] { 1 },
                new[] { 2 },
            },
            passengerWalks: new[] { new[] { 0 }, new[] { 0 }, new[] { 0 } },
            candidateCount: 3,
            matrix: matrix);

        var or = new OrToolsSolver(new SolverOptions(TimeBudgetMs: 3_000), Microsoft.Extensions.Logging.Abstractions.NullLogger<OrToolsSolver>.Instance);
        var sol = await or.SolveAsync(input, default);

        var arrivals = sol.Routes[0].Stops.Select(s => s.EstimatedArrival).ToList();
        for (var i = 1; i < arrivals.Count; i++)
        {
            Assert.True(arrivals[i] >= arrivals[i - 1], $"arrival time decreased at stop {i}");
        }
    }
}
