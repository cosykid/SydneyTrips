using NetTopologySuite.Geometries;

namespace Trips.Core.Abstractions;

/// <summary>
/// Typed client for the Google Routes API (Routes Preferred).
/// Implementation in <c>Trips.Integrations</c> applies aggressive field masking + Redis caching.
/// </summary>
public interface IGoogleRoutesClient
{
    /// <summary>
    /// Driving-time matrix in minutes between every origin and every destination.
    /// Computed once per optimisation run and cached. Both origins and destinations are typically
    /// the union of (driver-origin, candidate-nodes, trip-destination).
    /// </summary>
    /// <param name="trafficAware">
    /// When <c>true</c>, requests live-traffic durations — this selects Google's more expensive
    /// "Pro" Route Matrix SKU and is only worth it on the realtime ETA path, where the trip is
    /// happening now. The planner passes <c>false</c>: it solves against a future departure, so a
    /// live-traffic snapshot taken at plan time is noise, and free-flow durations bill at the
    /// cheaper "Essentials" rate and cache for far longer (they're stable geography).
    /// </param>
    Task<double[,]> ComputeRouteMatrixAsync(IReadOnlyList<Point> origins, IReadOnlyList<Point> destinations, bool trafficAware, CancellationToken ct);

    /// <summary>
    /// Compute one or more driving routes through a sequence of waypoints, optionally letting Google reorder them.
    /// Used by the post-processor to "snap" a heuristic-produced order to real Google directions.
    /// </summary>
    Task<GoogleRoutesResult> ComputeRoutesAsync(Point origin, Point destination, IReadOnlyList<Point> waypoints, bool optimizeWaypointOrder, CancellationToken ct);
}

/// <summary>Result of a route computation: legs, total time, total distance, and the order Google chose for waypoints.</summary>
public sealed record GoogleRoutesResult(
    IReadOnlyList<GoogleRouteLeg> Legs,
    double TotalDurationMins,
    double TotalDistanceMeters,
    IReadOnlyList<int>? OptimisedWaypointOrder);

public sealed record GoogleRouteLeg(Point From, Point To, double DurationMins, double DistanceMeters);
