using NetTopologySuite.Geometries;
using Trips.Core.Abstractions;

namespace Trips.Integrations.Clients;

/// <summary>
/// <see cref="IGoogleRoutesClient"/> that splits the travel-time matrix by traffic-awareness:
/// free-flow (planning) matrices go to a self-hosted <see cref="IFreeFlowMatrixClient"/> (OSRM) at
/// zero marginal cost, while traffic-aware (live ETA) matrices and the polyline
/// <see cref="ComputeRoutesAsync"/> stay on Google. This is the seam that takes the project's
/// dominant external cost — the per-element Route Matrix on the planning path — off Google entirely.
/// </summary>
/// <remarks>
/// When the free-flow source is unavailable the call is <em>not</em> silently re-routed to Google:
/// re-introducing billable calls on a transient OSRM blip is exactly the surprise cost we're
/// removing. Instead the failure propagates, and <c>OptimisationRunner.EnrichWithDrivingMatrixAsync</c>
/// keeps its haversine estimate for that one run — a bounded, free degradation. (Google is used for
/// free-flow only when no OSRM source is wired at all, i.e. <c>Integrations:Osrm:BaseUrl</c> unset.)
/// </remarks>
internal sealed class HybridRoutesClient : IGoogleRoutesClient
{
    private readonly IGoogleRoutesClient _google;
    private readonly IFreeFlowMatrixClient? _freeFlow;

    public HybridRoutesClient(IGoogleRoutesClient google, IFreeFlowMatrixClient? freeFlow)
    {
        ArgumentNullException.ThrowIfNull(google);
        _google = google;
        _freeFlow = freeFlow;
    }

    public Task<double[,]> ComputeRouteMatrixAsync(
        IReadOnlyList<Point> origins,
        IReadOnlyList<Point> destinations,
        bool trafficAware,
        CancellationToken ct)
    {
        // Traffic-aware (live ETA) stays on Google — OSRM has no live traffic. Free-flow (planning)
        // goes to OSRM when one is configured; otherwise to Google as before.
        if (!trafficAware && _freeFlow is not null)
        {
            return _freeFlow.ComputeMatrixAsync(origins, destinations, ct);
        }

        return _google.ComputeRouteMatrixAsync(origins, destinations, trafficAware, ct);
    }

    public Task<GoogleRoutesResult> ComputeRoutesAsync(
        Point origin,
        Point destination,
        IReadOnlyList<Point> waypoints,
        bool optimizeWaypointOrder,
        CancellationToken ct)
        // The polished polyline on a locked solution is low-volume (≤ once per lock) and a different
        // SKU; keep it on Google for quality.
        => _google.ComputeRoutesAsync(origin, destination, waypoints, optimizeWaypointOrder, ct);
}
