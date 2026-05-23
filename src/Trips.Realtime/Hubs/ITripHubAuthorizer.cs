using Trips.Core.Domain;

namespace Trips.Realtime.Hubs;

/// <summary>
/// Abstraction over the owner/participant authorisation check the hub needs. Implemented by
/// <c>Trips.Api.Auth.TripAuthorizationService</c> and registered by the API host. Kept here as an
/// interface so the hub doesn't have to reference Trips.Api directly — and so tests can inject a
/// stub authorizer without booting the full WebApplicationFactory if they want to.
/// </summary>
public interface ITripHubAuthorizer
{
    /// <summary>Returns the trip when the user is owner or a participant, otherwise <c>null</c>.</summary>
    Task<Trip?> AuthorizeAsync(Guid tripId, Guid userId, CancellationToken ct);
}
