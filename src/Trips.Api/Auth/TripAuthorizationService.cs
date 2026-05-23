using Trips.Core.Abstractions;

namespace Trips.Api.Auth;

/// <summary>
/// Centralised check for "is the calling user allowed to act on this trip?". Used by every
/// trip-scoped endpoint. A user can act on a trip if they are the owner OR a participant.
/// </summary>
public sealed class TripAuthorizationService
{
    private readonly ITripRepository _trips;
    private readonly IParticipantRepository _participants;

    public TripAuthorizationService(ITripRepository trips, IParticipantRepository participants)
    {
        ArgumentNullException.ThrowIfNull(trips);
        ArgumentNullException.ThrowIfNull(participants);
        _trips = trips;
        _participants = participants;
    }

    /// <summary>Returns the trip when the caller is authorised, otherwise null.</summary>
    public async Task<Core.Domain.Trip?> AuthorizeAsync(Guid tripId, Guid userId, CancellationToken ct)
    {
        if (userId == Guid.Empty)
        {
            return null;
        }

        var trip = await _trips.GetByIdAsync(tripId, ct).ConfigureAwait(false);
        if (trip is null)
        {
            return null;
        }
        if (trip.OwnerId == userId)
        {
            return trip;
        }

        var participants = await _participants.ListForTripAsync(tripId, ct).ConfigureAwait(false);
        return participants.Any(p => p.UserId == userId) ? trip : null;
    }
}
