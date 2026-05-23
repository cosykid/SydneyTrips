namespace Trips.Realtime.Hubs;

/// <summary>
/// Strongly-typed client contract for <see cref="TripHub"/> — every method here is invoked by the
/// server on connected clients via SignalR. The frontend (WS6 Phase C) consumes these via the
/// <c>@microsoft/signalr</c> client; the method names below are the wire contract and must not be
/// renamed without updating the frontend.
/// </summary>
public interface ITripHubClient
{
    /// <summary>Driver position update for the trip the connection has joined.</summary>
    Task DriverPositionUpdated(Guid driverId, double lat, double lng, DateTime ts);

    /// <summary>Recomputed ETA for one passenger after a driver-position or GTFS-RT update.</summary>
    Task EtaUpdated(Guid passengerId, DateTime newEta);

    /// <summary>A passenger has confirmed they are at the rendezvous stop.</summary>
    Task PassengerAtStop(Guid passengerId, Guid stopId, DateTime ts);

    /// <summary>A new locked solution has been computed for the trip (what-if / repair).</summary>
    Task RouteRecomputed(Guid tripId, Guid solutionId);

    /// <summary>High-level trip lifecycle change (started, en-route, completed, cancelled, ...).</summary>
    Task TripStatusChanged(Guid tripId, string status);
}
