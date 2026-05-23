using Microsoft.EntityFrameworkCore;
using Trips.Core.Abstractions;
using Trips.Core.Domain;

namespace Trips.Data.Repositories;

internal sealed class ParticipantRepository : IParticipantRepository
{
    private readonly TripsDbContext _db;

    public ParticipantRepository(TripsDbContext db)
    {
        _db = db;
    }

    public Task<Participant?> GetByIdAsync(Guid participantId, CancellationToken ct) =>
        _db.Participants.FirstOrDefaultAsync(p => p.Id == participantId, ct);

    public Task<Participant?> GetWithCandidateNodesAsync(Guid participantId, CancellationToken ct) =>
        _db.Participants
            .Include(p => p.CandidateNodes)
            .FirstOrDefaultAsync(p => p.Id == participantId, ct);

    public async Task<IReadOnlyList<Participant>> ListForTripAsync(Guid tripId, CancellationToken ct) =>
        await _db.Participants
            .Include(p => p.CandidateNodes)
            .Where(p => p.TripId == tripId)
            .ToListAsync(ct);

    public async Task AddAsync(Participant participant, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(participant);
        await _db.Participants.AddAsync(participant, ct);
    }

    public void Remove(Participant participant)
    {
        ArgumentNullException.ThrowIfNull(participant);
        _db.Participants.Remove(participant);
    }

    public Task<int> SaveChangesAsync(CancellationToken ct) => _db.SaveChangesAsync(ct);
}
