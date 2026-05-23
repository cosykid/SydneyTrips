using Microsoft.Extensions.Logging;
using Trips.Bench.Generator;
using Trips.Bench.Report;
using Trips.Bench.Runner;

// CLI: dotnet run --project bench/Trips.Bench -- --instances 60 --time-budget-ms 10000 --output bench/REPORT.md
var (instances, timeBudgetMs, output) = ParseArgs(args);

using var loggerFactory = LoggerFactory.Create(b => b.AddSimpleConsole(o =>
{
    o.SingleLine = true;
    o.TimestampFormat = "HH:mm:ss ";
}).SetMinimumLevel(LogLevel.Information));

var atlasPath = ResolveAtlasPath();
Console.WriteLine($"Loading suburb atlas from {atlasPath}");
var generator = InstanceGenerator.LoadFromJson(atlasPath);

var benchOpts = SizeForInstances(instances, timeBudgetMs);
Console.WriteLine($"Running {benchOpts.InstanceMatrix().Count} instances × 2 solvers @ {timeBudgetMs}ms each (≈ {benchOpts.InstanceMatrix().Count * 2 * timeBudgetMs / 1000 / 60} min upper bound)");

var runner = new BenchmarkRunner(generator, benchOpts, loggerFactory.CreateLogger<BenchmarkRunner>());
var sw = System.Diagnostics.Stopwatch.StartNew();
var results = await runner.RunAsync(CancellationToken.None);
sw.Stop();
Console.WriteLine($"Done in {sw.Elapsed:hh\\:mm\\:ss}.");

var outDir = Path.GetDirectoryName(Path.GetFullPath(output))!;
Directory.CreateDirectory(outDir);
var csvPath = Path.Combine(outDir, "results.csv");

new ReportWriter().Write(results, output, csvPath);
Console.WriteLine($"Wrote {output}");
Console.WriteLine($"Wrote {csvPath}");

return 0;

static (int instances, int timeBudgetMs, string output) ParseArgs(string[] args)
{
    int instances = 60;
    int timeBudgetMs = 10_000;
    string output = "bench/REPORT.md";
    for (var i = 0; i < args.Length; i++)
    {
        switch (args[i])
        {
            case "--instances":
                instances = int.Parse(args[++i]);
                break;
            case "--time-budget-ms":
                timeBudgetMs = int.Parse(args[++i]);
                break;
            case "--output":
                output = args[++i];
                break;
            case "--help" or "-h":
                Console.WriteLine("usage: Trips.Bench [--instances N] [--time-budget-ms MS] [--output PATH]");
                Environment.Exit(0);
                break;
        }
    }
    return (instances, timeBudgetMs, output);
}

static BenchOptions SizeForInstances(int instances, int timeBudgetMs)
{
    // Full plan: 4 passenger sizes × 3 driver sizes × 5 seeds = 60 instances.
    var pc = new[] { 5, 10, 20, 30 };
    var dc = new[] { 2, 3, 5 };
    var seeds = new[] { 11, 23, 37, 41, 53 };
    var full = pc.Length * dc.Length * seeds.Length; // 60
    if (instances >= full) return new BenchOptions(timeBudgetMs, pc, dc, seeds);

    // Smaller request — trim seeds first (preserves coverage of all instance classes), then trim sizes.
    if (instances >= 12)
    {
        var trimmedSeeds = seeds.Take(Math.Max(1, instances / (pc.Length * dc.Length))).ToArray();
        return new BenchOptions(timeBudgetMs, pc, dc, trimmedSeeds);
    }
    return new BenchOptions(timeBudgetMs, new[] { 5, 10 }, new[] { 2, 3 }, new[] { 11, 23, 37 });
}

static string ResolveAtlasPath()
{
    var candidates = new[]
    {
        Path.Combine(AppContext.BaseDirectory, "sydney-suburbs.json"),
        Path.Combine("bench", "sydney-suburbs.json"),
        Path.Combine("..", "..", "..", "..", "..", "bench", "sydney-suburbs.json"),
    };
    foreach (var c in candidates)
    {
        if (File.Exists(c)) return Path.GetFullPath(c);
    }
    throw new FileNotFoundException("sydney-suburbs.json not found; searched: " + string.Join(", ", candidates));
}
