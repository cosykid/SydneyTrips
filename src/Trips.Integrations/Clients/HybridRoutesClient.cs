using NetTopologySuite.Geometries;
using Trips.Core.Abstractions;

namespace Trips.Integrations.Clients;

/// <summary>
/// <see cref="IGoogleRoutesClient"/> that routes <em>every</em> travel-time matrix — planning
/// (free-flow) and live-ETA alike — to a self-hosted <see cref="IFreeFlowMatrixClient"/> (OSRM) at
/// zero marginal cost when one is configured, so Google's per-element Route Matrix is never called.
/// Only the polyline <see cref="ComputeRoutesAsync"/> (a separate, low-volume "Compute Routes" SKU)
/// stays on Google. With no OSRM configured, everything forwards to Google unchanged.
/// </summary>
/// <remarks>
/// OSRM has no live traffic, so the live-ETA matrix served here is a free-flow estimate rather than
/// a traffic-aware one — a deliberate trade for a hard zero on Google Route Matrix spend (the
/// project's dominant external cost). When the free-flow source is unavailable the call is
/// <em>not</em> re-routed to Google: re-introducing billable calls on a transient OSRM blip is
/// exactly the surprise cost we're removing. Instead the failure propagates and callers degrade
/// gracefully — <c>OptimisationRunner.EnrichWithDrivingMatrixAsync</c> keeps its haversine estimate;
/// <c>EtaService</c> skips that ETA broadcast. Google serves the matrix only when no OSRM source is
/// wired at all (<c>Integrations:Osrm:BaseUrl</c> unset).
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
        // When OSRM is configured it serves *every* matrix — planning and live ETA — so Google's
        // per-element Route Matrix is never called. OSRM has no live traffic, so trafficAware is
        // intentionally ignored: live ETAs become free-flow estimates, the deliberate trade for a
        // hard zero on Google matrix spend. Google is reached only when no OSRM is wired at all.
        if (_freeFlow is not null)
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
