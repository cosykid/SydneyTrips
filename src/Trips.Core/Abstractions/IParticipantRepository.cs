using Trips.Core.Domain;

namespace Trips.Core.Abstractions;

/// <summary>
/// Repository for participants of a trip. Loads candidate-node sets eagerly where useful.
/// </summary>
public interface IParticipantRepository
{
    Task<Participant?> GetByIdAsync(Guid participantId, CancellationToken ct);

    Task<Participant?> GetWithCandidateNodesAsync(Guid participantId, CancellationToken ct);

    Task<IReadOnlyList<Participant>> ListForTripAsync(Guid tripId, CancellationToken ct);

    Task AddAsync(Participant participant, CancellationToken ct);

    void Remove(Participant participant);

    Task<int> SaveChangesAsync(CancellationToken ct);
}
