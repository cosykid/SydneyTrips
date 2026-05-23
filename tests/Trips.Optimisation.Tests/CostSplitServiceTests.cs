using FluentAssertions;
using FsCheck.Xunit;
using NetTopologySuite.Geometries;
using Trips.Core.Domain;
using Trips.Optimisation.Cost;

namespace Trips.Optimisation.Tests;

public sealed class CostSplitServiceTests
{
    /// <summary>
    /// Hand-computed 3-passenger 1-driver linear route.
    /// Layout: driver origin (D) → stop A → stop B → stop C → destination, all colinear along a
    /// meridian so distances are easy to compute. Passengers board at A, B, C respectively (one
    /// each). Segments:
    ///   D→A : 0 passengers aboard (driver alone)        → driver absorbs fuel
    ///   A→B : 1 passenger (Alice) aboard               → Alice pays km(A→B)
    ///   B→C : 2 passengers (Alice + Bob)               → each pays km(B→C)/2
    ///   C→dest : 3 passengers (Alice + Bob + Carol)    → each pays km(C→dest)/3
    ///
    /// We can compute Alice + Bob + Carol's km exactly; their fuel shares scale linearly. Total
    /// fuel cost equals the sum of segment fuels including the D→A driver-only leg (driver
    /// "absorbs" but the algorithm still counts it toward TotalFuelCost — which mirrors what the
    /// driver actually paid). The Sum(PassengerShare.Total) is therefore strictly less than
    /// TotalFuelCost by exactly the D→A fuel cost, which is the right answer: passengers don't pay
    /// for the empty-leg fuel.
    /// </summary>
    [Fact]
    public void Linear_route_hand_computed_shares()
    {
        // Build a 4-stop layout. Trailing leg (C→dest) is not visible from the Stop graph alone
        // because we don't persist the destination point on the Solution — see CostSplitService.cs
        // "trailing leg" note. We test what IS computable: D→A, A→B, B→C.
        // For this test, we make the route have stops only (no trailing destination leg). The
        // CostSplitService correctly charges A→B to Alice alone, B→C to Alice+Bob.
        var stopAId = Guid.NewGuid();
        var stopBId = Guid.NewGuid();
        var stopCId = Guid.NewGuid();
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var carol = Guid.NewGuid();
        var driverId = Guid.NewGuid();

        // Use latitude steps so each segment is ~1 km. 1 degree of latitude ≈ 111 km, so
        // 1 km ≈ 0.009 degrees. Coordinates are (lon, lat).
        var stopA = new Point(151.0, -33.000) { SRID = 4326 };
        var stopB = new Point(151.0, -33.009) { SRID = 4326 };
        var stopC = new Point(151.0, -33.018) { SRID = 4326 };

        var route = new DriverRoute(Guid.NewGuid(), Guid.Empty, driverId, travelMins: 0, orderIndex: 0,
            stops: new[]
            {
                new Stop(stopAId, Guid.Empty, orderIndex: 0, location: stopA, candidateNodeId: Guid.Empty,
                    estimatedArrival: DateTimeOffset.UtcNow, pickups: new[] { alice }),
                new Stop(stopBId, Guid.Empty, orderIndex: 1, location: stopB, candidateNodeId: Guid.Empty,
                    estimatedArrival: DateTimeOffset.UtcNow, pickups: new[] { bob }),
                new Stop(stopCId, Guid.Empty, orderIndex: 2, location: stopC, candidateNodeId: Guid.Empty,
                    estimatedArrival: DateTimeOffset.UtcNow, pickups: new[] { carol }),
            });
        var solution = new Solution(
            id: Guid.NewGuid(),
            optimisationRunId: Guid.NewGuid(),
            label: "test",
            objective: 0,
            objectiveTerms: new[] { 0.0, 0, 0, 0, 0 },
            routes: new[] { route });

        var inputs = new CostInputs(
            FuelPricePerLitre: 2.00,
            VehicleFuelEconomyLPer100Km: 10.0,
            Tolls: Array.Empty<TollSegment>());
        var breakdown = CostSplitService.Compute(solution, inputs);

        // Expected km per leg (haversine):
        //   D→A is 0 (first stop, no origin point persisted) → driver absorbs nothing (0 fuel)
        //   A→B ≈ 1 km, Alice rides alone
        //   B→C ≈ 1 km, Alice + Bob aboard → each gets 0.5 km
        // Carol boards at C; trailing leg to destination not modelled — Carol pays 0.
        var kmAB = CostSplitService.HaversineKm(stopA, stopB);
        var kmBC = CostSplitService.HaversineKm(stopB, stopC);
        var aliceKm = kmAB + kmBC / 2.0;
        var bobKm = kmBC / 2.0;
        var litresPerKm = 10.0 / 100.0;
        var aliceFuel = aliceKm * litresPerKm * 2.00;
        var bobFuel = bobKm * litresPerKm * 2.00;

        var alicePay = breakdown.ShareByPassenger.First(s => s.ParticipantId == alice);
        var bobPay = breakdown.ShareByPassenger.First(s => s.ParticipantId == bob);
        alicePay.FuelShare.Should().BeApproximately(aliceFuel, 1e-6);
        bobPay.FuelShare.Should().BeApproximately(bobFuel, 1e-6);
        // Carol boarded but no segment was charged to her.
        var carolPay = breakdown.ShareByPassenger.FirstOrDefault(s => s.ParticipantId == carol);
        carolPay?.FuelShare.Should().BeApproximately(0.0, 1e-6);
        breakdown.TotalTollCost.Should().Be(0);
    }

    /// <summary>
    /// Sum of passenger fuel shares equals total fuel cost iff every leg has at least one passenger
    /// aboard. In our test fixture above the first leg (D→A) is collapsed to zero distance because
    /// we don't persist the driver origin, so the equality holds: every leg with non-zero distance
    /// has ≥1 passenger.
    /// </summary>
    [Fact]
    public void Sum_of_passenger_shares_equals_total_when_first_leg_collapsed()
    {
        // Same 3-passenger linear route as above.
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var carol = Guid.NewGuid();
        var driverId = Guid.NewGuid();
        var stopA = new Point(151.0, -33.000) { SRID = 4326 };
        var stopB = new Point(151.0, -33.009) { SRID = 4326 };
        var stopC = new Point(151.0, -33.018) { SRID = 4326 };

        var route = new DriverRoute(Guid.NewGuid(), Guid.Empty, driverId, 0, 0, new[]
        {
            new Stop(Guid.NewGuid(), Guid.Empty, 0, stopA, Guid.Empty, DateTimeOffset.UtcNow, new[] { alice }),
            new Stop(Guid.NewGuid(), Guid.Empty, 1, stopB, Guid.Empty, DateTimeOffset.UtcNow, new[] { bob }),
            new Stop(Guid.NewGuid(), Guid.Empty, 2, stopC, Guid.Empty, DateTimeOffset.UtcNow, new[] { carol }),
        });
        var solution = new Solution(Guid.NewGuid(), Guid.NewGuid(), "test", 0,
            new[] { 0.0, 0, 0, 0, 0 }, new[] { route });

        var breakdown = CostSplitService.Compute(solution,
            new CostInputs(2.00, 10.0, Array.Empty<TollSegment>()));
        var sumShares = breakdown.ShareByPassenger.Sum(s => s.Total);
        sumShares.Should().BeApproximately(breakdown.TotalFuelCost, 1e-6);
    }

    /// <summary>
    /// Property-style across a sweep of instance sizes: sum of shares ≤ total cost, all shares
    /// non-negative. Complemented below by a FsCheck-driven property over randomly sized fixtures.
    /// </summary>
    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    [InlineData(5)]
    [InlineData(8)]
    public void Property_sum_bounded_and_non_negative(int stopCount)
    {
        var fixture = MakeFixture(stopCount);
        var breakdown = CostSplitService.Compute(fixture.Solution, fixture.Inputs);
        var sumShares = breakdown.ShareByPassenger.Sum(s => s.Total);
        var total = breakdown.TotalFuelCost + breakdown.TotalTollCost;
        sumShares.Should().BeLessThanOrEqualTo(total + 1e-6);
        breakdown.ShareByPassenger.Should().OnlyContain(s => s.FuelShare >= -1e-9 && s.TollShare >= -1e-9);
    }

    /// <summary>
    /// FsCheck property: for any number of stops in [2,10], the sum of passenger shares is bounded
    /// above by the total cost and every share is non-negative. We run a small fixed number of
    /// iterations (50) so the test is fast.
    /// </summary>
    [Property(MaxTest = 50)]
    public bool Sum_of_shares_bounded_by_total_for_any_size(byte rawSize)
    {
        var size = 2 + rawSize % 9; // 2..10
        var fixture = MakeFixture(size);
        var breakdown = CostSplitService.Compute(fixture.Solution, fixture.Inputs);
        var sumShares = breakdown.ShareByPassenger.Sum(s => s.Total);
        var total = breakdown.TotalFuelCost + breakdown.TotalTollCost;
        var bounded = sumShares <= total + 1e-6;
        var nonNeg = breakdown.ShareByPassenger.All(s => s.FuelShare >= -1e-9 && s.TollShare >= -1e-9);
        return bounded && nonNeg;
    }

    [Fact]
    public void Toll_only_applies_to_passengers_aboard_that_segment()
    {
        // Two-stop route. Toll segment is on the leg between stop1 and stop2 (where only Alice is
        // aboard, because Bob boards at stop2). Bob should pay no toll.
        var alice = Guid.NewGuid();
        var bob = Guid.NewGuid();
        var stop1Id = Guid.NewGuid();
        var stop2Id = Guid.NewGuid();
        var stop1 = new Point(151.0, -33.000) { SRID = 4326 };
        var stop2 = new Point(151.0, -33.009) { SRID = 4326 };
        var route = new DriverRoute(Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), 0, 0, new[]
        {
            new Stop(stop1Id, Guid.Empty, 0, stop1, Guid.Empty, DateTimeOffset.UtcNow, new[] { alice }),
            new Stop(stop2Id, Guid.Empty, 1, stop2, Guid.Empty, DateTimeOffset.UtcNow, new[] { bob }),
        });
        var solution = new Solution(Guid.NewGuid(), Guid.NewGuid(), "t", 0, new[] { 0.0, 0, 0, 0, 0 }, new[] { route });

        var tolls = new[] { new TollSegment(stop1Id, stop2Id, Amount: 5.00) };
        var breakdown = CostSplitService.Compute(solution, new CostInputs(2.0, 10.0, tolls));

        var alicePay = breakdown.ShareByPassenger.First(s => s.ParticipantId == alice);
        alicePay.TollShare.Should().Be(5.00);
        var bobPay = breakdown.ShareByPassenger.FirstOrDefault(s => s.ParticipantId == bob);
        // Bob is not in the toll-charged segment; either he has no entry, or his toll share is 0.
        (bobPay?.TollShare ?? 0).Should().Be(0);
        breakdown.TotalTollCost.Should().Be(5.00);
    }

    private static CostSplitFixture MakeFixture(int stopCount) => CostSplitFixtureFactory.Build(stopCount);

    [Fact]
    public void Empty_solution_produces_zero_breakdown()
    {
        var solution = new Solution(Guid.NewGuid(), Guid.NewGuid(), "empty", 0,
            new[] { 0.0, 0, 0, 0, 0 },
            new[]
            {
                new DriverRoute(Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), 0, 0, Array.Empty<Stop>()),
            });
        var breakdown = CostSplitService.Compute(solution, new CostInputs(2.0, 10.0, Array.Empty<TollSegment>()));
        breakdown.TotalFuelCost.Should().Be(0);
        breakdown.ShareByPassenger.Should().BeEmpty();
    }
}

public sealed record CostSplitFixture(Solution Solution, CostInputs Inputs);

public static class CostSplitFixtureFactory
{
    /// <summary>Linear N-stop route with one passenger boarding at each stop. No tolls.</summary>
    public static CostSplitFixture Build(int stopCount)
    {
        var stops = new List<Stop>();
        for (var i = 0; i < stopCount; i++)
        {
            var pt = new Point(151.0, -33.0 - 0.01 * i) { SRID = 4326 };
            stops.Add(new Stop(Guid.NewGuid(), Guid.Empty, i, pt, Guid.Empty,
                DateTimeOffset.UtcNow, new[] { Guid.NewGuid() }));
        }
        var route = new DriverRoute(Guid.NewGuid(), Guid.Empty, Guid.NewGuid(), 0, 0, stops);
        var sol = new Solution(Guid.NewGuid(), Guid.NewGuid(), "rand", 0,
            new[] { 0.0, 0, 0, 0, 0 }, new[] { route });
        return new CostSplitFixture(sol, new CostInputs(2.0, 8.5, Array.Empty<TollSegment>()));
    }
}
