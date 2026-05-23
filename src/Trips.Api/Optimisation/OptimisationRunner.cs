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

    private static ISolver ResolveSolver(IServiceProvider sp, SolverKind kind)
    {
        var solvers = sp.GetServices<ISolver>().ToList();
        if (solvers.Count == 0)
        {
            return sp.GetRequiredService<ISolver>();
        }
        return solvers.FirstOrDefault(s => s.Kind == kind) ?? solvers[0];
    }

    private static SolverInput BuildSolverInput(Trip trip, OptimisationRun run)
    {
        // Construct a flat node list: [destination, ...participantHomes].
        // The matrix is sparse/synthetic — solvers from WS3 will overwrite this in production.
        var nodes = new List<SolverNode>
        {
            new(0, NodeKind.Home, CandidateNodeId: null),
        };

        var drivers = new List<SolverDriver>();
        var passengers = new List<SolverPassenger>();

        var index = 1;
        foreach (var participant in trip.Participants)
        {
            var participantNodeIndex = index++;
            nodes.Add(new SolverNode(participantNodeIndex, NodeKind.Home, CandidateNodeId: null));

            if (participant.HasCar)
            {
                drivers.Add(new SolverDriver(participant.Id, participantNodeIndex, participant.Seats));
            }
            else
            {
                passengers.Add(new SolverPassenger(
                    ParticipantId: participant.Id,
                    CandidateNodeIndices: new[] { participantNodeIndex },
                    WalkPtMinsByNodeIndex: new[] { 0 }));
            }
        }

        if (drivers.Count == 0 && trip.Participants.Count > 0)
        {
            // Promote the first participant to a synthetic driver so the solver has something to assign to.
            drivers.Add(new SolverDriver(trip.Participants[0].Id, OriginNodeIndex: 1, Seats: Math.Max(1, trip.Participants.Count)));
        }

        var n = nodes.Count;
        var matrix = new double[n, n];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
            {
                matrix[i, j] = i == j ? 0.0 : 10.0;
            }
        }

        return new SolverInput(
            RunId: run.Id,
            TripId: trip.Id,
            Weights: run.Weights,
            Drivers: drivers,
            Passengers: passengers,
            Nodes: nodes,
            TravelMatrix: matrix,
            DepartAt: trip.DepartAt);
    }

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
