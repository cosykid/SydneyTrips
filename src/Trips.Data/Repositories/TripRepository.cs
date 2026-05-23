using Microsoft.EntityFrameworkCore;
using Trips.Core.Abstractions;
using Trips.Core.Domain;

namespace Trips.Data.Repositories;

internal sealed class TripRepository : ITripRepository
{
    private readonly TripsDbContext _db;

    public TripRepository(TripsDbContext db)
    {
        _db = db;
    }

    public Task<Trip?> GetByIdAsync(Guid tripId, CancellationToken ct) =>
        _db.Trips.FirstOrDefaultAsync(t => t.Id == tripId, ct);

    public Task<Trip?> GetWithParticipantsAsync(Guid tripId, CancellationToken ct) =>
        _db.Trips
            .Include(t => t.Participants)
                .ThenInclude(p => p.CandidateNodes)
            .FirstOrDefaultAsync(t => t.Id == tripId, ct);

    public Task<Trip?> GetWithRunsAsync(Guid tripId, CancellationToken ct) =>
        _db.Trips
            .Include(t => t.Runs)
            .FirstOrDefaultAsync(t => t.Id == tripId, ct);

    public async Task<IReadOnlyList<Trip>> ListForOwnerAsync(Guid ownerId, CancellationToken ct) =>
        await _db.Trips
            .Include(t => t.Participants)
            .Where(t => t.OwnerId == ownerId)
            .OrderByDescending(t => t.DepartAt)
            .ToListAsync(ct);

    public async Task AddAsync(Trip trip, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(trip);
        await _db.Trips.AddAsync(trip, ct);
    }

    public void Remove(Trip trip)
    {
        ArgumentNullException.ThrowIfNull(trip);
        _db.Trips.Remove(trip);
    }

    public Task<int> SaveChangesAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);
}
