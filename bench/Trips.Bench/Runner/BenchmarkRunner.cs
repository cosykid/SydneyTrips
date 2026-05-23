using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Trips.Bench.Generator;
using Trips.Core.Abstractions;
using Trips.Core.Domain;
using Trips.Optimisation.Common;
using Trips.Optimisation.Heuristic;
using Trips.Optimisation.OrTools;

namespace Trips.Bench.Runner;

/// <summary>
/// Executes the bench plan: for every instance in <see cref="InstanceMatrix"/>, runs both solvers
/// with the same wall-clock budget and records objective, per-term breakdown, runtime, stop count,
/// heuristic iterations, and gap to OR-Tools' best-found objective.
///
/// The runner is deterministic given the seeds in <see cref="BenchOptions.Seeds"/>; deterministic
/// is important because we report results into a markdown file that should be reproducible.
/// </summary>
public sealed class BenchmarkRunner
{
    private readonly InstanceGenerator _generator;
    private readonly BenchOptions _opts;
    private readonly ILogger<BenchmarkRunner> _logger;

    public BenchmarkRunner(InstanceGenerator generator, BenchOptions opts, ILogger<BenchmarkRunner> logger)
    {
        _generator = generator;
        _opts = opts;
        _logger = logger;
    }

    public async Task<IReadOnlyList<BenchResult>> RunAsync(CancellationToken ct)
    {
        var instanceMatrix = _opts.InstanceMatrix();
        var results = new List<BenchResult>(instanceMatrix.Count);
        var solverOptions = new SolverOptions(TimeBudgetMs: _opts.TimeBudgetMs, LogProgress: false, RandomSeed: 42);
        var schedule = SimulatedAnnealingSchedule.Default;
        var index = 0;

        foreach (var (passengerCount, driverCount, seed) in instanceMatrix)
        {
            index++;
            ct.ThrowIfCancellationRequested();
            var instance = _generator.Generate(passengerCount, driverCount, seed);
            _logger.LogInformation("[{Idx}/{Total}] {Cls} seed={Seed}", index, instanceMatrix.Count, instance.Metadata.ClassLabel, seed);

            // OR-Tools
            var ort = new OrToolsSolver(solverOptions, NullLogger<OrToolsSolver>.Instance);
            var ortSw = Stopwatch.StartNew();
            var ortSol = await ort.SolveAsync(instance.Input, ct).ConfigureAwait(false);
            ortSw.Stop();
            var ortStats = ort.LastStats;

            // Heuristic
            var heur = new HeuristicSolver(solverOptions, schedule, NullLogger<HeuristicSolver>.Instance);
            var heurSw = Stopwatch.StartNew();
            var heurSol = await heur.SolveAsync(instance.Input, ct).ConfigureAwait(false);
            heurSw.Stop();
            var heurStats = heur.LastStats;

            results.Add(new BenchResult(
                Instance: instance.Metadata,
                OrToolsObjective: ortSol.Objective,
                OrToolsTerms: ortSol.ObjectiveTerms,
                OrToolsRuntimeMs: ortSw.ElapsedMilliseconds,
                OrToolsStops: CountStops(ortSol),
                OrToolsStatus: ortStats.Status.ToString(),
                OrToolsGap: ortStats.Gap,
                OrToolsBranches: ortStats.Branches,
                HeuristicObjective: heurSol.Objective,
                HeuristicTerms: heurSol.ObjectiveTerms,
                HeuristicRuntimeMs: heurSw.ElapsedMilliseconds,
                HeuristicStops: CountStops(heurSol),
                HeuristicIterations: heurStats.Iterations,
                HeuristicAcceptanceRate: heurStats.AcceptanceRate,
                HeuristicConvergence: heurStats.ConvergenceTrace,
                OrToolsSolution: ortSol,
                HeuristicSolution: heurSol));
        }

        return results;
    }

    private static int CountStops(Solution s) => s.Routes.Sum(r => r.Stops.Count);
}

/// <summary>Knobs for a bench run.</summary>
/// <param name="TimeBudgetMs">Wall-clock cap per solver invocation. 10s by default.</param>
/// <param name="PassengerCounts">Passenger sizes to try.</param>
/// <param name="DriverCounts">Driver sizes to try.</param>
/// <param name="Seeds">Seeds for each (passenger, driver) combination.</param>
public sealed record BenchOptions(
    int TimeBudgetMs = 10_000,
    int[]? PassengerCounts = null,
    int[]? DriverCounts = null,
    int[]? Seeds = null)
{
    public IReadOnlyList<(int passengers, int drivers, int seed)> InstanceMatrix()
    {
        var pc = PassengerCounts ?? new[] { 5, 10, 20, 30 };
        var dc = DriverCounts ?? new[] { 2, 3, 5 };
        var seeds = Seeds ?? new[] { 11, 23, 37, 41, 53 };
        var list = new List<(int, int, int)>();
        foreach (var p in pc)
            foreach (var d in dc)
                foreach (var s in seeds)
                    list.Add((p, d, s));
        return list;
    }
}

/// <summary>One instance's full result for the bench report.</summary>
public sealed record BenchResult(
    InstanceMetadata Instance,
    double OrToolsObjective,
    double[] OrToolsTerms,
    long OrToolsRuntimeMs,
    int OrToolsStops,
    string OrToolsStatus,
    double OrToolsGap,
    long OrToolsBranches,
    double HeuristicObjective,
    double[] HeuristicTerms,
    long HeuristicRuntimeMs,
    int HeuristicStops,
    int HeuristicIterations,
    double HeuristicAcceptanceRate,
    IReadOnlyList<(int Iteration, double BestObjective)> HeuristicConvergence,
    Solution OrToolsSolution,
    Solution HeuristicSolution)
{
    public double GapPercent => OrToolsObjective == 0 ? 0 : (HeuristicObjective - OrToolsObjective) / OrToolsObjective * 100.0;
}
