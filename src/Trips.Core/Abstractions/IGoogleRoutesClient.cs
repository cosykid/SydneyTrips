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
    Task<double[,]> ComputeRouteMatrixAsync(IReadOnlyList<Point> origins, IReadOnlyList<Point> destinations, CancellationToken ct);

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
