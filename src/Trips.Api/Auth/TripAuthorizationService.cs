using Trips.Core.Abstractions;
using Trips.Realtime.Hubs;

namespace Trips.Api.Auth;

/// <summary>
/// Trip lookup for endpoints + the realtime hub. With anonymous-session auth there is no
/// per-user gate on reads or writes — anyone with the trip id (e.g. a passenger following a
/// share link) can act on it. The only thing the cookie GUID controls is "is this trip in my
/// list" and "can I delete it" (owner-only).
/// </summary>
public sealed class TripAuthorizationService : ITripHubAuthorizer
{
    private readonly ITripRepository _trips;

    public TripAuthorizationService(ITripRepository trips)
    {
        ArgumentNullException.ThrowIfNull(trips);
        _trips = trips;
    }

    /// <summary>Returns the trip if it exists, otherwise null. No ownership check.</summary>
    public async Task<Core.Domain.Trip?> LookupAsync(Guid tripId, CancellationToken ct) =>
        await _trips.GetByIdAsync(tripId, ct).ConfigureAwait(false);

    /// <inheritdoc cref="ITripHubAuthorizer.LookupAsync"/>
    Task<Core.Domain.Trip?> ITripHubAuthorizer.LookupAsync(Guid tripId, CancellationToken ct) =>
        LookupAsync(tripId, ct);
}
