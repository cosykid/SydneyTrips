using Microsoft.EntityFrameworkCore;
using Trips.Core.Abstractions;
using Trips.Core.Domain;

namespace Trips.Data.Repositories;

internal sealed class TripEventRepository : ITripEventRepository
{
    private readonly TripsDbContext _db;

    public TripEventRepository(TripsDbContext db)
    {
        _db = db;
    }

    public async Task AddAsync(TripEvent evt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(evt);
        await _db.TripEvents.AddAsync(evt, ct);
    }

    public async Task<IReadOnlyList<TripEvent>> ListForTripAsync(Guid tripId, CancellationToken ct) =>
        await _db.TripEvents
            .Where(e => e.TripId == tripId)
            .OrderBy(e => e.Timestamp)
            .ToListAsync(ct);

    public async Task<IReadOnlyList<TripEvent>> ListSinceAsync(Guid tripId, DateTimeOffset since, CancellationToken ct) =>
        await _db.TripEvents
            .Where(e => e.TripId == tripId && e.Timestamp >= since)
            .OrderBy(e => e.Timestamp)
            .ToListAsync(ct);

    public Task<int> SaveChangesAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);
}
