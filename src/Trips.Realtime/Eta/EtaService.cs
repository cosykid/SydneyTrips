using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;
using Trips.Core.Abstractions;
using Trips.Core.Domain;
using Trips.Realtime.Hubs;

namespace Trips.Realtime.Eta;

/// <summary>
/// Recomputes per-passenger ETAs given a fresh driver position and broadcasts the result to the
/// trip's SignalR group. Reads the locked solution off <see cref="IOptimisationRunRepository"/> and
/// derives the driver's remaining stop list from it; new ETAs are obtained from
/// <see cref="IGoogleRoutesClient.ComputeRouteMatrixAsync"/> (row = new origin, columns = remaining
/// stops). Failure to compute (no locked solution, no client) is logged at debug and swallowed —
/// ETA is a "nice to have" alongside the raw position broadcast that already went out.
/// </summary>
public sealed class EtaService
{
    private static readonly GeometryFactory GeometryFactory = new(new PrecisionModel(), 4326);

    private readonly ITripRepository _trips;
    private readonly IOptimisationRunRepository _runs;
    private readonly IGoogleRoutesClient _routes;
    private readonly IHubContext<TripHub, ITripHubClient> _hubContext;
    private readonly IClock _clock;
    private readonly ILogger<EtaService> _logger;

    public EtaService(
        ITripRepository trips,
        IOptimisationRunRepository runs,
        IGoogleRoutesClient routes,
        IHubContext<TripHub, ITripHubClient> hubContext,
        IClock clock,
        ILogger<EtaService> logger)
    {
        ArgumentNullException.ThrowIfNull(trips);
        ArgumentNullException.ThrowIfNull(runs);
        ArgumentNullException.ThrowIfNull(routes);
        ArgumentNullException.ThrowIfNull(hubContext);
        ArgumentNullException.ThrowIfNull(clock);
        ArgumentNullException.ThrowIfNull(logger);
        _trips = trips;
        _runs = runs;
        _routes = routes;
        _hubContext = hubContext;
        _clock = clock;
        _logger = logger;
    }

    /// <summary>
    /// Compute fresh ETAs for every passenger downstream of the driver and broadcast
    /// <c>EtaUpdated</c> to the trip's SignalR group. The driver's position determines the matrix
    /// origin; the driver's remaining stops on the locked solution determine the matrix destinations.
    /// </summary>
    public async Task RecomputeAndBroadcastAsync(
        Guid tripId,
        Guid driverId,
        double driverLat,
        double driverLng,
        CancellationToken ct)
    {
        var trip = await _trips.GetByIdAsync(tripId, ct).ConfigureAwait(false);
        if (trip is null)
        {
            _logger.LogDebug("ETA recompute skipped: trip {Trip} not found", tripId);
            return;
        }
        if (trip.LockedSolutionId is null)
        {
            _logger.LogDebug("ETA recompute skipped: trip {Trip} has no locked solution", tripId);
            return;
        }

        // Walk the run history to find the run that owns the locked solution.
        var runs = await _runs.ListForTripAsync(tripId, ct).ConfigureAwait(false);
        OptimisationRun? owningRun = null;
        foreach (var r in runs)
        {
            var withSolutions = await _runs.GetWithSolutionsAsync(r.Id, ct).ConfigureAwait(false);
            if (withSolutions is not null && withSolutions.Solutions.Any(s => s.Id == trip.LockedSolutionId.Value))
            {
                owningRun = withSolutions;
                break;
            }
        }
        if (owningRun is null)
        {
            _logger.LogDebug("ETA recompute skipped: locked solution {Solution} not found", trip.LockedSolutionId);
            return;
        }

        var solution = owningRun.Solutions.First(s => s.Id == trip.LockedSolutionId!.Value);
        var route = solution.Routes.FirstOrDefault(r => r.DriverId == driverId);
        if (route is null || route.Stops.Count == 0)
        {
            _logger.LogDebug("ETA recompute skipped: no route for driver {Driver} on trip {Trip}", driverId, tripId);
            return;
        }

        // Build the origin from the driver's reported position; collect the destination points from
        // the remaining stops in order.
        var origin = GeometryFactory.CreatePoint(new Coordinate(driverLng, driverLat));
        var orderedStops = route.Stops.OrderBy(s => s.OrderIndex).ToList();
        var destinations = orderedStops.Select(s => s.Location).ToList();

        double[,] matrix;
        try
        {
            // trafficAware: true expresses the intent — the trip is happening now, so a live-traffic
            // ETA is ideal. Whether it's honoured depends on the wiring: with OSRM configured the
            // HybridRoutesClient serves this from OSRM (a free-flow estimate, no live traffic) to keep
            // Google's Route Matrix at zero spend; only a Google-only deployment pays the pricier SKU.
            matrix = await _routes.ComputeRouteMatrixAsync(new[] { origin }, destinations, trafficAware: true, ct).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "ETA recompute: matrix call failed for trip {Trip}", tripId);
            return;
        }

        if (matrix.GetLength(0) < 1 || matrix.GetLength(1) < orderedStops.Count)
        {
            _logger.LogWarning(
                "ETA recompute: matrix shape mismatch ({Rows}x{Cols}) for {Stops} stops",
                matrix.GetLength(0),
                matrix.GetLength(1),
                orderedStops.Count);
            return;
        }

        // Sum the per-leg minutes — the matrix gives time from driver-origin to each stop, but we
        // care about cumulative time *along the planned route*. We walk the row [origin → stop_0]
        // and approximate later legs with the planned EstimatedArrival deltas; this keeps us within
        // a single matrix call. (The full per-leg matrix would be O(n^2) calls which isn't worth it
        // for live ETA tracking.)
        var anchor = _clock.UtcNow;
        var firstStopEta = anchor.AddMinutes(matrix[0, 0]);
        var plannedFirstArrival = orderedStops[0].EstimatedArrival;
        var shift = firstStopEta - plannedFirstArrival;

        var group = _hubContext.Clients.Group(TripHub.GroupName(tripId));
        for (var i = 0; i < orderedStops.Count; i++)
        {
            var stop = orderedStops[i];
            var newEta = (stop.EstimatedArrival + shift).UtcDateTime;

            foreach (var passengerId in stop.Pickups)
            {
                await group.EtaUpdated(passengerId, newEta).ConfigureAwait(false);
            }
        }
    }
}
