using Trips.Core.Domain;

namespace Trips.Realtime.Hubs;

/// <summary>
/// Trip-lookup hook used by <see cref="TripHub"/>. With anonymous-session auth there's no
/// per-user gate on hub operations — the hub just needs to know the trip exists. Implemented by
/// <c>Trips.Api.Auth.TripAuthorizationService</c> in the API host. Kept as an interface so the
/// hub doesn't have to reference Trips.Api directly, and so tests can inject a stub if they want.
/// </summary>
public interface ITripHubAuthorizer
{
    /// <summary>Returns the trip if it exists, otherwise <c>null</c>.</summary>
    Task<Trip?> LookupAsync(Guid tripId, CancellationToken ct);
}
