using NetTopologySuite.Geometries;

namespace Trips.Core.Abstractions;

/// <summary>
/// Typed client for the Transport for NSW Open Data API.
/// Implementation lives in <c>Trips.Integrations</c> and is wired with caching + retry.
/// </summary>
public interface ITfNswClient
{
    /// <summary>Plan a public-transport trip between two coordinates. Backed by EFA Trip Planner v2 (rapidJSON).</summary>
    Task<TfNswTripPlan> TripPlanAsync(Point origin, Point destination, DateTimeOffset departAt, CancellationToken ct);

    /// <summary>
    /// Stops, stations, and POIs within <paramref name="radiusMeters"/> of a point, ordered by distance.
    /// This is the primary source of <see cref="Trips.Core.Domain.CandidateNode"/>s for a participant.
    /// </summary>
    Task<IReadOnlyList<TfNswCoordinateStop>> CoordinateRequestAsync(Point origin, int radiusMeters, CancellationToken ct);

    /// <summary>Live departures for a single stop / station.</summary>
    Task<IReadOnlyList<TfNswDeparture>> DepartureAsync(string stopId, DateTimeOffset @from, CancellationToken ct);

    /// <summary>GTFS-Realtime trip updates as an async stream, scoped to a mode (e.g. "buses", "trains").</summary>
    IAsyncEnumerable<TfNswGtfsTripUpdate> GtfsRtTripUpdatesAsync(string mode, CancellationToken ct);
}

/// <summary>Result of a TfNSW trip plan: a journey made of legs, each with mode + travel minutes.</summary>
public sealed record TfNswTripPlan(IReadOnlyList<TfNswJourneyLeg> Legs, int TotalWalkMins, int TotalPtMins);

public sealed record TfNswJourneyLeg(string Mode, int DurationMins, Point From, Point To, string? RouteShortName);

/// <summary>One coordinate-request hit — a stop or station near a point.</summary>
public sealed record TfNswCoordinateStop(string StopId, string Name, Point Location, int DistanceMeters, string Mode);

public sealed record TfNswDeparture(string StopId, string Line, DateTimeOffset PlannedDeparture, DateTimeOffset EstimatedDeparture, string? VehicleId);

public sealed record TfNswGtfsTripUpdate(string TripId, string? VehicleId, DateTimeOffset Timestamp, IReadOnlyList<TfNswStopTimeUpdate> StopTimeUpdates);

public sealed record TfNswStopTimeUpdate(string StopId, DateTimeOffset? Arrival, DateTimeOffset? Departure);
