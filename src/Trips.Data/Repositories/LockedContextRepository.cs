using Microsoft.EntityFrameworkCore;
using Trips.Core.Abstractions;
using Trips.Core.Domain;

namespace Trips.Data.Repositories;

/// <summary>
/// Builds a <see cref="LockedContext"/> for what-if re-optimisation: the locked <see cref="Solution"/>
/// plus a reconstructed <see cref="SolverInput"/> consistent with the original run.
///
/// <para>We don't persist <see cref="SolverInput"/> itself (it's a derived value of the trip + run),
/// so this repository rebuilds it from the trip's participants and the run's weights. The candidate
/// nodes are reconstructed from each participant's persisted <see cref="CandidateNode"/> set. The
/// travel matrix uses a coarse haversine-driven estimate; for production accuracy the matrix should
/// be re-fetched from <see cref="IGoogleRoutesClient"/> here, but that's an optimisation we defer to
/// the runner — the warm-start hint biases search regardless of matrix precision.</para>
/// </summary>
internal sealed class LockedContextRepository : ILockedContextRepository
{
    private readonly TripsDbContext _db;

    public LockedContextRepository(TripsDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public async Task<LockedContext?> GetByIdAsync(Guid lockedSolutionId, CancellationToken ct)
    {
        var solution = await _db.Solutions
            .Include(s => s.Routes)
                .ThenInclude(r => r.Stops)
            .FirstOrDefaultAsync(s => s.Id == lockedSolutionId, ct).ConfigureAwait(false);
        if (solution is null) return null;

        var run = await _db.OptimisationRuns.FirstOrDefaultAsync(r => r.Id == solution.OptimisationRunId, ct)
            .ConfigureAwait(false);
        if (run is null) return null;

        var trip = await _db.Trips
            .Include(t => t.Participants)
                .ThenInclude(p => p.CandidateNodes)
            .FirstOrDefaultAsync(t => t.Id == run.TripId, ct).ConfigureAwait(false);
        if (trip is null) return null;

        var input = SolverInputBuilder.Build(trip, run);
        return new LockedContext(solution, input);
    }

    /// <summary>
    /// Back-compat shim retained for WhatIfService and tests that still call the old static. Delegates
    /// to <see cref="SolverInputBuilder.Build"/> which now owns the canonical construction path.
    /// </summary>
    public static SolverInput BuildSolverInput(Trip trip, OptimisationRun run)
        => SolverInputBuilder.Build(trip, run);
}
