using Microsoft.EntityFrameworkCore;
using Trips.Core.Abstractions;
using Trips.Core.Domain;

namespace Trips.Data.Repositories;

/// <summary>
/// EF-backed adapter for <see cref="ISolutionRepository"/>. Eagerly loads the solution graph
/// (routes + stops) so the cost-split service can iterate without re-querying.
/// </summary>
internal sealed class SolutionRepository : ISolutionRepository
{
    private readonly TripsDbContext _db;

    public SolutionRepository(TripsDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public Task<Solution?> GetByIdAsync(Guid solutionId, CancellationToken ct) =>
        _db.Solutions
            .Include(s => s.Routes)
                .ThenInclude(r => r.Stops)
            .FirstOrDefaultAsync(s => s.Id == solutionId, ct);
}
