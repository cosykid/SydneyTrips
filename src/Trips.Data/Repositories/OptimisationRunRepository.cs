using Microsoft.EntityFrameworkCore;
using Trips.Core.Abstractions;
using Trips.Core.Domain;

namespace Trips.Data.Repositories;

internal sealed class OptimisationRunRepository : IOptimisationRunRepository
{
    private readonly TripsDbContext _db;

    public OptimisationRunRepository(TripsDbContext db)
    {
        _db = db;
    }

    public Task<OptimisationRun?> GetByIdAsync(Guid runId, CancellationToken ct) =>
        _db.OptimisationRuns.FirstOrDefaultAsync(r => r.Id == runId, ct);

    public Task<OptimisationRun?> GetWithSolutionsAsync(Guid runId, CancellationToken ct) =>
        _db.OptimisationRuns
            .Include(r => r.Solutions)
                .ThenInclude(s => s.Routes)
                    .ThenInclude(dr => dr.Stops)
            .FirstOrDefaultAsync(r => r.Id == runId, ct);

    public async Task<IReadOnlyList<OptimisationRun>> ListForTripAsync(Guid tripId, CancellationToken ct) =>
        await _db.OptimisationRuns
            .Where(r => r.TripId == tripId)
            .OrderByDescending(r => r.StartedAt)
            .ToListAsync(ct);

    public async Task AddAsync(OptimisationRun run, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(run);
        await _db.OptimisationRuns.AddAsync(run, ct);
    }

    public Task<int> SaveChangesAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);
}
