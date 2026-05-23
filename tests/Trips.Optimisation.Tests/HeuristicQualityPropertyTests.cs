using Trips.Core.Abstractions;
using Trips.Core.Domain;
using Trips.Optimisation.Common;
using Trips.Optimisation.Heuristic;
using Trips.Optimisation.OrTools;
using Trips.Optimisation.Tests.Helpers;

namespace Trips.Optimisation.Tests;

/// <summary>
/// Pseudo-property tests: for several seeded random feasible instances, the heuristic's objective
/// is within 50% of OR-Tools' best-found objective. The 50% bound is intentionally loose — the
/// spec calls it out as a "very loose sanity check" — its job is to catch regressions where the
/// heuristic becomes catastrophically worse than OR-Tools, not to verify tight optimality.
///
/// <para>We use xUnit <c>[Theory]</c> with seeded data rather than a property-based framework
/// (FsCheck/Hedgehog) so the tests are deterministic and run in CI without extra dependencies.
/// The seeded inputs are essentially the same as a property-based test would generate, but the
/// reproducibility is better.</para>
/// </summary>
public class HeuristicQualityPropertyTests
{
    [Theory]
    [InlineData(2, 1, 11)]
    [InlineData(3, 2, 23)]
    [InlineData(4, 2, 37)]
    [InlineData(5, 2, 41)]
    [InlineData(5, 3, 53)]
    public async Task HeuristicWithinFiftyPercentOfOrTools(int passengerCount, int driverCount, int seed)
    {
        var instance = BuildInstance(passengerCount, driverCount, seed);

        var or = new OrToolsSolver(new SolverOptions(TimeBudgetMs: 2_500), Microsoft.Extensions.Logging.Abstractions.NullLogger<OrToolsSolver>.Instance);
        var heur = new HeuristicSolver(new SolverOptions(TimeBudgetMs: 2_500), SimulatedAnnealingSchedule.Default, Microsoft.Extensions.Logging.Abstractions.NullLogger<HeuristicSolver>.Instance);

        var orSol = await or.SolveAsync(instance, default);
        var heurSol = await heur.SolveAsync(instance, default);

        Assert.True(double.IsFinite(orSol.Objective), $"OR-Tools returned non-finite objective for ({passengerCount}p,{driverCount}d,seed={seed})");
        Assert.True(double.IsFinite(heurSol.Objective), "Heuristic returned non-finite objective");

        // Loose bound: heuristic ≤ 1.5 × OR-Tools' best found. Heuristic often *beats* OR-Tools on
        // larger instances where OR-Tools times out; we assert nothing about that direction.
        var bound = orSol.Objective * 1.5;
        Assert.True(heurSol.Objective <= bound,
            $"Heuristic obj {heurSol.Objective:F2} exceeds 1.5× OR-Tools obj {orSol.Objective:F2} for ({passengerCount}p,{driverCount}d,seed={seed})");
    }

    private static SolverInput BuildInstance(int passengerCount, int driverCount, int seed)
    {
        var rng = new Random(seed);
        var candidatesPerPassenger = 2;
        var candidateCount = passengerCount * candidatesPerPassenger;
        var totalNodes = driverCount + candidateCount + 1;

        var matrix = new double[totalNodes, totalNodes];
        for (var i = 0; i < totalNodes; i++)
            for (var j = i + 1; j < totalNodes; j++)
            {
                var t = 3.0 + rng.NextDouble() * 25.0;
                matrix[i, j] = t; matrix[j, i] = t;
            }

        var passengerCandidatesLocal = new int[passengerCount][];
        var passengerWalks = new int[passengerCount][];
        for (var p = 0; p < passengerCount; p++)
        {
            passengerCandidatesLocal[p] = new int[candidatesPerPassenger];
            passengerWalks[p] = new int[candidatesPerPassenger];
            for (var k = 0; k < candidatesPerPassenger; k++)
            {
                passengerCandidatesLocal[p][k] = p * candidatesPerPassenger + k;
                passengerWalks[p][k] = rng.Next(0, 8);
            }
        }

        var driverSeats = new int[driverCount];
        for (var d = 0; d < driverCount; d++) driverSeats[d] = 4;

        return InstanceBuilder.Build(
            driverCount, driverSeats,
            passengerCandidatesLocal, passengerWalks,
            candidateCount, matrix);
    }
}
