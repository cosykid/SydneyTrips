using System.Globalization;
using System.Text;
using Trips.Bench.Generator;
using Trips.Bench.Runner;

namespace Trips.Bench.Report;

/// <summary>
/// Renders the bench results into a markdown report, a CSV side-channel, and (where useful) ASCII
/// or mermaid visualisations.
///
/// <para>The report has these sections:</para>
/// <list type="number">
///   <item><b>How to read this</b> — orientation for someone landing here from the README.</item>
///   <item><b>Summary table</b> — per instance class (5p/2d, …), average OR-Tools / Heuristic
///   objective + gap % + median runtimes.</item>
///   <item><b>Detailed results</b> — every row in the matrix with its instance metadata.</item>
///   <item><b>Two hand-picked example solutions</b> — rendered as mermaid graphs (driver paths
///   through stops, one colour per driver).</item>
///   <item><b>Heuristic convergence</b> — ASCII sparklines of best-objective vs iteration for
///   2 representative instances.</item>
///   <item><b>Raw data pointer</b> — link to <c>results.csv</c> for plotting elsewhere.</item>
/// </list>
/// </summary>
public sealed class ReportWriter
{
    public void Write(IReadOnlyList<BenchResult> results, string markdownPath, string csvPath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(markdownPath)!);
        WriteCsv(results, csvPath);
        WriteMarkdown(results, markdownPath, csvPath);
    }

    private static void WriteCsv(IReadOnlyList<BenchResult> results, string path)
    {
        var sb = new StringBuilder();
        sb.AppendLine("class,seed,destination,or_obj,or_drive,or_stops_term,or_walk,or_spread,or_fairness,or_runtime_ms,or_status,or_gap,or_branches,or_stops,heur_obj,heur_drive,heur_stops_term,heur_walk,heur_spread,heur_fairness,heur_runtime_ms,heur_stops,heur_iters,heur_accept,gap_pct");
        foreach (var r in results)
        {
            sb.AppendLine(string.Join(',',
                r.Instance.ClassLabel,
                r.Instance.Seed,
                r.Instance.DestinationName.Replace(',', ';'),
                F(r.OrToolsObjective),
                F(r.OrToolsTerms[0]),
                F(r.OrToolsTerms[1]),
                F(r.OrToolsTerms[2]),
                F(r.OrToolsTerms[3]),
                F(r.OrToolsTerms[4]),
                r.OrToolsRuntimeMs,
                r.OrToolsStatus,
                F(r.OrToolsGap),
                r.OrToolsBranches,
                r.OrToolsStops,
                F(r.HeuristicObjective),
                F(r.HeuristicTerms[0]),
                F(r.HeuristicTerms[1]),
                F(r.HeuristicTerms[2]),
                F(r.HeuristicTerms[3]),
                F(r.HeuristicTerms[4]),
                r.HeuristicRuntimeMs,
                r.HeuristicStops,
                r.HeuristicIterations,
                F(r.HeuristicAcceptanceRate),
                F(r.GapPercent)));
        }
        File.WriteAllText(path, sb.ToString());
    }

    private static void WriteMarkdown(IReadOnlyList<BenchResult> results, string mdPath, string csvPath)
    {
        var sb = new StringBuilder();
        sb.AppendLine("# Sydney Group-Trip Optimisation — Benchmark Report");
        sb.AppendLine();
        sb.AppendLine($"_Generated {DateTimeOffset.UtcNow:u} from {results.Count} instances._");
        sb.AppendLine();

        // ---- Headline ---------------------------------------------------------------------------
        var classes = results.GroupBy(r => r.Instance.ClassLabel).OrderBy(g => Order(g.Key)).ToList();
        var avgGap = results.Where(r => double.IsFinite(r.GapPercent)).Select(r => r.GapPercent).DefaultIfEmpty(0).Average();
        var medianOrt = Median(results.Select(r => (double)r.OrToolsRuntimeMs));
        var medianHeur = Median(results.Select(r => (double)r.HeuristicRuntimeMs));
        sb.AppendLine("## Headline");
        sb.AppendLine();
        sb.AppendLine($"- **Average gap (heuristic vs OR-Tools)**: {avgGap:F2}%");
        sb.AppendLine($"- **Median runtime (OR-Tools)**: {medianOrt:F0} ms");
        sb.AppendLine($"- **Median runtime (Heuristic)**: {medianHeur:F0} ms");
        sb.AppendLine($"- **Total instances**: {results.Count} across {classes.Count} classes");
        sb.AppendLine();

        // ---- How to read ------------------------------------------------------------------------
        sb.AppendLine("## How to read this");
        sb.AppendLine();
        sb.AppendLine("Each row in the **Summary table** averages over multiple seeds for one instance class (e.g. \"10p/3d\" = 10 passengers, 3 drivers).");
        sb.AppendLine();
        sb.AppendLine("- **OR-Tools obj** is the best objective found by the CP-SAT model within the wall-clock budget. If OR-Tools terminates with `Optimal` status it is *the* optimum; otherwise it's just a feasible upper bound.");
        sb.AppendLine("- **Heuristic obj** is the best objective from cheapest-insertion construction + simulated-annealing local search.");
        sb.AppendLine("- **Gap %** is `(heur − ortools) / ortools × 100`. Negative means the heuristic beat OR-Tools' best-found (only possible when OR-Tools timed out before reaching optimal).");
        sb.AppendLine("- **Obj** is a weighted sum of five terms — drive, stops, walk+PT, arrival-spread, fairness — using `ObjectiveWeights.Balanced`. The two solvers use the *exact same* `ObjectiveEvaluator` so the values are directly comparable.");
        sb.AppendLine();
        sb.AppendLine("Travel times are synthetic: haversine × 1.25 congestion multiplier. The bench does not call the real Google Routes API — see `InstanceGenerator.cs` for the model.");
        sb.AppendLine();

        // ---- Summary table ----------------------------------------------------------------------
        sb.AppendLine("## Summary table (per instance class)");
        sb.AppendLine();
        sb.AppendLine("| Class | n | OR-Tools obj | Heur obj | Gap % | OR-Tools ms | Heur ms | OR-Tools stops | Heur stops |");
        sb.AppendLine("|-------|---|-------------:|---------:|------:|------------:|--------:|---------------:|-----------:|");
        foreach (var g in classes)
        {
            var n = g.Count();
            var avgOrt = g.Average(r => r.OrToolsObjective);
            var avgHeur = g.Average(r => r.HeuristicObjective);
            var avgGapCls = g.Where(r => double.IsFinite(r.GapPercent)).Select(r => r.GapPercent).DefaultIfEmpty(0).Average();
            var medOrt = Median(g.Select(r => (double)r.OrToolsRuntimeMs));
            var medHeur = Median(g.Select(r => (double)r.HeuristicRuntimeMs));
            var avgOrtStops = g.Average(r => r.OrToolsStops);
            var avgHeurStops = g.Average(r => r.HeuristicStops);
            sb.AppendLine($"| {g.Key} | {n} | {avgOrt:F2} | {avgHeur:F2} | {avgGapCls:F2} | {medOrt:F0} | {medHeur:F0} | {avgOrtStops:F1} | {avgHeurStops:F1} |");
        }
        sb.AppendLine();

        // ---- Detailed results -------------------------------------------------------------------
        sb.AppendLine("## Detailed results (one row per instance)");
        sb.AppendLine();
        sb.AppendLine("| Class | Seed | Destination | OR-Tools obj | OR status | OR ms | Heur obj | Heur ms | Heur iters | Gap % |");
        sb.AppendLine("|-------|------|-------------|-------------:|-----------|------:|---------:|--------:|-----------:|------:|");
        foreach (var r in results.OrderBy(r => Order(r.Instance.ClassLabel)).ThenBy(r => r.Instance.Seed))
        {
            sb.AppendLine($"| {r.Instance.ClassLabel} | {r.Instance.Seed} | {r.Instance.DestinationName} | {r.OrToolsObjective:F2} | {r.OrToolsStatus} | {r.OrToolsRuntimeMs} | {r.HeuristicObjective:F2} | {r.HeuristicRuntimeMs} | {r.HeuristicIterations:N0} | {r.GapPercent:F2} |");
        }
        sb.AppendLine();

        // ---- Two example solutions --------------------------------------------------------------
        sb.AppendLine("## Hand-picked example solutions");
        sb.AppendLine();
        var samples = PickExamples(results);
        foreach (var (label, res) in samples)
        {
            sb.AppendLine($"### {label}: {res.Instance.ClassLabel} seed={res.Instance.Seed} → {res.Instance.DestinationName}");
            sb.AppendLine();
            sb.AppendLine($"**OR-Tools** objective={res.OrToolsObjective:F2} terms=[drive={res.OrToolsTerms[0]:F2}, stops={res.OrToolsTerms[1]:F2}, walk={res.OrToolsTerms[2]:F2}, spread={res.OrToolsTerms[3]:F2}, fair={res.OrToolsTerms[4]:F2}] ms={res.OrToolsRuntimeMs}");
            sb.AppendLine();
            sb.AppendLine("```mermaid");
            sb.AppendLine(RenderMermaid(res.OrToolsSolution, res.Instance, prefix: "ort"));
            sb.AppendLine("```");
            sb.AppendLine();
            sb.AppendLine($"**Heuristic** objective={res.HeuristicObjective:F2} terms=[drive={res.HeuristicTerms[0]:F2}, stops={res.HeuristicTerms[1]:F2}, walk={res.HeuristicTerms[2]:F2}, spread={res.HeuristicTerms[3]:F2}, fair={res.HeuristicTerms[4]:F2}] ms={res.HeuristicRuntimeMs} iters={res.HeuristicIterations:N0}");
            sb.AppendLine();
            sb.AppendLine("```mermaid");
            sb.AppendLine(RenderMermaid(res.HeuristicSolution, res.Instance, prefix: "heur"));
            sb.AppendLine("```");
            sb.AppendLine();
        }

        // ---- Convergence curves -----------------------------------------------------------------
        sb.AppendLine("## Heuristic convergence (ASCII sparkline)");
        sb.AppendLine();
        sb.AppendLine("Best objective so far, sampled over the iteration history. The curve is the running minimum. A flat line means SA accepted no improving moves; a step-down marks a basin escape.");
        sb.AppendLine();
        foreach (var (label, res) in samples)
        {
            sb.AppendLine($"**{label}: {res.Instance.ClassLabel} seed={res.Instance.Seed}**");
            sb.AppendLine();
            sb.AppendLine("```");
            sb.AppendLine(RenderConvergenceAscii(res.HeuristicConvergence));
            sb.AppendLine("```");
            sb.AppendLine();
        }

        // ---- Raw CSV pointer --------------------------------------------------------------------
        sb.AppendLine($"## Raw data");
        sb.AppendLine();
        sb.AppendLine($"All per-instance numbers are in [`{Path.GetFileName(csvPath)}`]({Path.GetFileName(csvPath)}). Columns include per-term objective values, OR-Tools status/gap/branches, heuristic iteration count and acceptance rate, and stop counts.");
        sb.AppendLine();

        File.WriteAllText(mdPath, sb.ToString());
    }

    private static string F(double v) => double.IsFinite(v) ? v.ToString("F4", CultureInfo.InvariantCulture) : "inf";

    private static int Order(string classLabel)
    {
        // "5p/2d" -> 5*100 + 2 so 5p/2d < 5p/3d < 10p/2d ...
        var bits = classLabel.Replace("p", "").Replace("d", "").Split('/');
        return int.Parse(bits[0], CultureInfo.InvariantCulture) * 100 + int.Parse(bits[1], CultureInfo.InvariantCulture);
    }

    private static double Median(IEnumerable<double> xs)
    {
        var arr = xs.ToArray();
        if (arr.Length == 0) return 0;
        Array.Sort(arr);
        return arr.Length % 2 == 1 ? arr[arr.Length / 2] : (arr[arr.Length / 2 - 1] + arr[arr.Length / 2]) / 2.0;
    }

    private static (string Label, BenchResult Result)[] PickExamples(IReadOnlyList<BenchResult> results)
    {
        // Pick one small instance (5p/2d) and one larger one (20p/3d if available, else 10p/3d) to
        // illustrate both regimes.
        var small = results.FirstOrDefault(r => r.Instance.PassengerCount == 5 && r.Instance.DriverCount == 2) ?? results[0];
        var large = results.FirstOrDefault(r => r.Instance.PassengerCount == 20 && r.Instance.DriverCount == 3)
                 ?? results.FirstOrDefault(r => r.Instance.PassengerCount == 10 && r.Instance.DriverCount == 3)
                 ?? results[results.Count - 1];
        return new[]
        {
            ("Small instance", small),
            ("Larger instance", large),
        };
    }

    private static string RenderMermaid(Trips.Core.Domain.Solution solution, InstanceMetadata meta, string prefix)
    {
        var sb = new StringBuilder();
        sb.AppendLine("graph LR");
        for (var d = 0; d < solution.Routes.Count; d++)
        {
            var route = solution.Routes[d];
            var prev = $"{prefix}_d{d}_origin([\"D{d} start\"])";
            sb.AppendLine($"  {prev}");
            for (var i = 0; i < route.Stops.Count; i++)
            {
                var node = $"{prefix}_d{d}_s{i}[\"stop {i + 1}<br/>{route.Stops[i].Pickups.Count} pickup(s)\"]";
                sb.AppendLine($"  {prev} --> {node}");
                prev = node;
            }
            sb.AppendLine($"  {prev} --> {prefix}_d{d}_dest{{{{\"{meta.DestinationName}\"}}}}");
        }
        return sb.ToString();
    }

    private static string RenderConvergenceAscii(IReadOnlyList<(int Iteration, double BestObjective)> trace)
    {
        if (trace.Count == 0) return "(empty)";
        const int width = 80;
        const int height = 12;
        var minObj = trace.Min(t => t.BestObjective);
        var maxObj = trace.Max(t => t.BestObjective);
        if (Math.Abs(maxObj - minObj) < 1e-9)
        {
            return $"flat at {minObj:F3} ({trace.Count} samples)";
        }
        var firstIter = trace[0].Iteration;
        var lastIter = trace[^1].Iteration;
        var iterRange = Math.Max(1, lastIter - firstIter);

        var grid = new char[height, width];
        for (var r = 0; r < height; r++)
            for (var c = 0; c < width; c++)
                grid[r, c] = ' ';

        // Plot points (continuous step function — the running min)
        for (var c = 0; c < width; c++)
        {
            var iter = firstIter + (long)((double)c / width * iterRange);
            var idx = FindLowerBound(trace, iter);
            var obj = trace[idx].BestObjective;
            var row = (int)Math.Round((1.0 - (obj - minObj) / (maxObj - minObj)) * (height - 1));
            if (row < 0) row = 0; if (row >= height) row = height - 1;
            grid[row, c] = '*';
        }

        var sb = new StringBuilder();
        sb.AppendLine($"max obj {maxObj:F2}  →  min obj {minObj:F2}  ({trace.Count} improving steps, last at iter {lastIter:N0})");
        for (var r = 0; r < height; r++)
        {
            var line = new char[width];
            for (var c = 0; c < width; c++) line[c] = grid[r, c];
            sb.AppendLine(new string(line));
        }
        sb.AppendLine($"iter {firstIter,8} {"…",30}{lastIter,38:N0}");
        return sb.ToString();
    }

    private static int FindLowerBound(IReadOnlyList<(int Iteration, double BestObjective)> trace, long target)
    {
        var lo = 0; var hi = trace.Count - 1;
        var best = 0;
        while (lo <= hi)
        {
            var mid = (lo + hi) / 2;
            if (trace[mid].Iteration <= target) { best = mid; lo = mid + 1; } else hi = mid - 1;
        }
        return best;
    }
}
