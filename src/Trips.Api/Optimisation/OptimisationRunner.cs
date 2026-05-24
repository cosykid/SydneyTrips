using System.Diagnostics;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Options;
using Trips.Core.Abstractions;
using Trips.Core.Domain;
using Trips.Data;

namespace Trips.Api.Optimisation;

/// <summary>
/// Background service that consumes optimisation jobs from <see cref="OptimisationJobQueue"/>
/// and executes them on the configured <see cref="ISolver"/>. Concurrency is bounded by
/// <see cref="OptimisationOptions.MaxConcurrent"/>; failures are persisted on the run row.
/// </summary>
public sealed class OptimisationRunner : BackgroundService
{
    private readonly OptimisationJobQueue _queue;
    private readonly IServiceScopeFactory _scopeFactory;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<OptimisationRunner> _logger;
    private readonly IClock _clock;
    private readonly OptimisationOptions _options;

    public OptimisationRunner(
        OptimisationJobQueue queue,
        IServiceScopeFactory scopeFactory,
        IHostApplicationLifetime lifetime,
        ILogger<OptimisationRunner> logger,
        IClock clock,
        IOptions<OptimisationOptions> options)
    {
        ArgumentNullException.ThrowIfNull(queue);
        ArgumentNullException.ThrowIfNull(scopeFactory);
        ArgumentNullException.ThrowIfNull(lifetime);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(options);
        _queue = queue;
        _scopeFactory = scopeFactory;
        _lifetime = lifetime;
        _logger = logger;
        _clock = clock;
        _options = options.Value;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken, _lifetime.ApplicationStopping);
        var degree = Math.Max(1, _options.MaxConcurrent);
        _logger.LogInformation("OptimisationRunner started with degree {Degree}", degree);

        var workers = Enumerable.Range(0, degree).Select(i => Task.Run(() => RunWorkerAsync(i, linked.Token), linked.Token)).ToArray();
        await Task.WhenAll(workers).ConfigureAwait(false);
    }

    private async Task RunWorkerAsync(int workerIndex, CancellationToken ct)
    {
        await foreach (var job in _queue.Reader.ReadAllAsync(ct).ConfigureAwait(false))
        {
            try
            {
                await ExecuteJobAsync(job, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                _logger.LogInformation("Worker {Worker} cancelled while running job {Run}", workerIndex, job.RunId);
                await MarkCancelledAsync(job.RunId).ConfigureAwait(false);
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Worker {Worker} failed running job {Run}", workerIndex, job.RunId);
                await MarkFailedAsync(job.RunId, ex.Message).ConfigureAwait(false);
            }
        }
    }

    private async Task ExecuteJobAsync(OptimisationJob job, CancellationToken ct)
    {
        using var scope = _scopeFactory.CreateScope();
        var sp = scope.ServiceProvider;

        var db = sp.GetRequiredService<TripsDbContext>();
        var trips = sp.GetRequiredService<ITripRepository>();
        var solver = ResolveSolver(sp, job.Solver);

        var run = await db.OptimisationRuns.FirstOrDefaultAsync(r => r.Id == job.RunId, ct).ConfigureAwait(false);
        if (run is null)
        {
            _logger.LogWarning("Run {Run} was not found; skipping", job.RunId);
            return;
        }

        run.MarkRunning();
        await db.SaveChangesAsync(ct).ConfigureAwait(false);

        var trip = await trips.GetWithParticipantsAsync(job.TripId, ct).ConfigureAwait(false);
        if (trip is null)
        {
            run.MarkFailed("Trip not found.", _clock.UtcNow);
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            return;
        }

        var input = BuildSolverInput(trip, run);
        // Warm-start support: when repairHint=true and the trip has a locked solution, fetch the
        // LockedContext and apply an empty delta so the SolverInput carries a warm-start hint. This
        // is what the what-if endpoint relies on for minimal-disruption re-optimisation. We
        // intentionally apply the empty delta (no drops/adds, original weights) here because the
        // current OptimisationJob contract doesn't carry a delta payload — the caller can re-issue
        // with explicit weights via OptimiseRequest if they want a different objective.
        if (job.RepairHint && trip.LockedSolutionId is { } lockedId)
        {
            var lockedContexts = sp.GetService<ILockedContextRepository>();
            if (lockedContexts is not null)
            {
                var ctx = await lockedContexts.GetByIdAsync(lockedId, ct).ConfigureAwait(false);
                if (ctx is not null)
                {
                    input = Trips.Optimisation.WhatIf.WhatIfService.ApplyDelta(ctx, new Trips.Optimisation.WhatIf.WhatIfDelta(
                        DropParticipantIds: null,
                        AddParticipants: null,
                        NewWeights: run.Weights));
                    // ApplyDelta gives us a fresh RunId; reset it to the job's run id so the produced
                    // solution belongs to the correct OptimisationRun row.
                    input = input with { RunId = run.Id, TripId = trip.Id };
                    _logger.LogInformation("Run {Run}: warm-start hint applied from locked solution {Solution}", job.RunId, lockedId);
                }
            }
        }
        // Snap the builder's crow-fly matrix to real Google driving minutes before solving. The
        // solver's assignment decisions are only as good as the costs it sees; a straight-line
        // estimate is fine for relative ordering on one landmass but wrong across the harbour,
        // rivers, and motorway-only corridors — exactly the geography that makes "consolidate onto
        // one driver" look cheaper than it really is. Applied after the warm-start block so both
        // cold-start and what-if inputs get real costs. Cached, so re-plans on the same node-set
        // are free; on any failure we keep the haversine estimate rather than failing the run.
        var routes = sp.GetService<IGoogleRoutesClient>();
        if (routes is not null)
        {
            input = await EnrichWithDrivingMatrixAsync(input, routes, ct).ConfigureAwait(false);
        }

        var sw = Stopwatch.StartNew();
        var solution = await solver.SolveAsync(input, ct).ConfigureAwait(false);
        sw.Stop();

        var stats = new OptimisationStats(
            WallClock: sw.Elapsed,
            IterationsOrNodes: 1,
            BestObjective: solution.Objective,
            LpRelaxation: null,
            Solver: solver.Kind);

        run.MarkCompleted(solution, paretoAlternatives: null, stats, _clock.UtcNow);
        // Explicitly add the new solution graph so EF marks the Solution + Routes + Stops as Added
        // rather than trying to UPDATE them based on their (already-set) primary keys.
        db.Solutions.Add(solution);
        await db.SaveChangesAsync(ct).ConfigureAwait(false);
        _logger.LogInformation("Run {Run} completed in {Ms}ms with objective {Obj}", job.RunId, sw.ElapsedMilliseconds, solution.Objective);
    }

    /// <summary>
    /// Replace the SolverInput's crow-fly travel matrix with real Google driving minutes. Google
    /// values are merged <em>over</em> the haversine matrix so any pair Google can't route (returns
    /// a non-finite duration for) keeps its finite estimate — the CP-SAT model scales the matrix by
    /// 100 and rounds, so a single <see cref="double.PositiveInfinity"/> would blow up the
    /// objective. Origins are batched to stay under Google's 625-element computeRouteMatrix cap.
    /// </summary>
    private async Task<SolverInput> EnrichWithDrivingMatrixAsync(SolverInput input, IGoogleRoutesClient routes, CancellationToken ct)
    {
        var n = input.Nodes.Count;
        if (n == 0)
        {
            return input;
        }

        var points = input.Nodes.Select(node => node.Location).ToList();
        var merged = (double[,])input.TravelMatrix.Clone();
        try
        {
            var maxOriginsPerCall = Math.Max(1, 625 / n);
            for (var start = 0; start < n; start += maxOriginsPerCall)
            {
                ct.ThrowIfCancellationRequested();
                var count = Math.Min(maxOriginsPerCall, n - start);
                var originBatch = points.GetRange(start, count);
                var sub = await routes.ComputeRouteMatrixAsync(originBatch, points, ct).ConfigureAwait(false);
                for (var oi = 0; oi < count; oi++)
                {
                    for (var di = 0; di < n; di++)
                    {
                        if (start + oi == di)
                        {
                            merged[start + oi, di] = 0.0;
                            continue;
                        }
                        var v = sub[oi, di];
                        if (double.IsFinite(v) && v >= 0.0)
                        {
                            merged[start + oi, di] = v;
                        }
                    }
                }
            }
            _logger.LogInformation("Run {Run}: travel matrix snapped to Google driving times ({N} nodes)", input.RunId, n);
            return input with { TravelMatrix = merged };
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Run {Run}: Google route matrix unavailable; keeping haversine estimate", input.RunId);
            return input;
        }
    }

    private static ISolver ResolveSolver(IServiceProvider sp, SolverKind kind)
    {
        var solvers = sp.GetServices<ISolver>().ToList();
        if (solvers.Count == 0)
        {
            return sp.GetRequiredService<ISolver>();
        }
        return solvers.FirstOrDefault(s => s.Kind == kind) ?? solvers[0];
    }

    // SolverInput construction is shared with LockedContextRepository via SolverInputBuilder so
    // both the cold-start and what-if paths feed the solver the same node layout — critically,
    // they both feed *candidate nodes* as pickup points (not just participant homes), which is what
    // lets passengers be picked up at transit hubs instead of doorsteps.
    private static SolverInput BuildSolverInput(Trip trip, OptimisationRun run)
        => SolverInputBuilder.Build(trip, run);

    private async Task MarkFailedAsync(Guid runId, string reason)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TripsDbContext>();
            var run = await db.OptimisationRuns.FirstOrDefaultAsync(r => r.Id == runId, CancellationToken.None).ConfigureAwait(false);
            if (run is null)
            {
                return;
            }
            run.MarkFailed(string.IsNullOrWhiteSpace(reason) ? "Unknown error." : reason, _clock.UtcNow);
            await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to mark run {Run} as failed", runId);
        }
    }

    private async Task MarkCancelledAsync(Guid runId)
    {
        try
        {
            using var scope = _scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<TripsDbContext>();
            var run = await db.OptimisationRuns.FirstOrDefaultAsync(r => r.Id == runId, CancellationToken.None).ConfigureAwait(false);
            if (run is null)
            {
                return;
            }
            run.MarkCancelled(_clock.UtcNow);
            await db.SaveChangesAsync(CancellationToken.None).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Failed to mark run {Run} as cancelled", runId);
        }
    }
}

/// <summary>Bound to the <c>Optimisation</c> configuration block.</summary>
public sealed class OptimisationOptions
{
    public const string SectionName = "Optimisation";
    public int MaxConcurrent { get; set; } = 2;
}
