using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using Trips.Core.Abstractions;
using Trips.Core.Domain;

namespace Trips.Optimisation.Postprocess;

/// <summary>
/// Optional final step on top of any <see cref="ISolver"/> output. The solvers operate on a
/// precomputed matrix; the postprocessor "snaps" that to real driving directions via
/// <see cref="IGoogleRoutesClient"/> so the persisted <see cref="Stop.EstimatedArrival"/> reflects
/// genuine road conditions.
///
/// <para>If no routes client is registered (the common case during testing and benchmarking) the
/// postprocessor returns the input solution unchanged. This keeps the matrix-only path correct.</para>
/// </summary>
public sealed class SolutionPostprocessor
{
    private readonly IGoogleRoutesClient? _routes;
    private readonly ILogger<SolutionPostprocessor> _logger;

    public SolutionPostprocessor()
        : this(null, NullLogger<SolutionPostprocessor>.Instance)
    {
    }

    public SolutionPostprocessor(IGoogleRoutesClient? routes, ILogger<SolutionPostprocessor> logger)
    {
        _routes = routes;
        _logger = logger ?? NullLogger<SolutionPostprocessor>.Instance;
    }

    /// <summary>
    /// Snap travel times in <paramref name="solution"/> to actual Google Routes results.
    /// </summary>
    /// <param name="solution">The matrix-based solution to refine.</param>
    /// <param name="departAt">Departure anchor for re-stamping arrival times.</param>
    /// <param name="driverOrigins">Per-driver WGS84 origin points (same order as the solution's routes).</param>
    /// <param name="destination">Trip destination point.</param>
    /// <param name="ct">Cancellation.</param>
    public async Task<Solution> PostprocessAsync(
        Solution solution,
        DateTimeOffset departAt,
        IReadOnlyDictionary<Guid, Point> driverOrigins,
        Point destination,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(solution);
        ArgumentNullException.ThrowIfNull(driverOrigins);
        ArgumentNullException.ThrowIfNull(destination);

        if (_routes is null)
        {
            _logger.LogDebug("Postprocessor: no IGoogleRoutesClient registered, returning matrix-only solution.");
            return solution;
        }

        var refinedRoutes = new List<DriverRoute>(solution.Routes.Count);
        foreach (var route in solution.Routes)
        {
            if (!driverOrigins.TryGetValue(route.DriverId, out var origin))
            {
                refinedRoutes.Add(route);
                continue;
            }
            var waypoints = route.Stops.Select(s => s.Location).ToList();
            var googleResult = await _routes.ComputeRoutesAsync(origin, destination, waypoints, optimizeWaypointOrder: false, ct).ConfigureAwait(false);

            // Re-stamp ETAs along the leg sequence.
            var cursor = departAt;
            var stops = new List<Stop>(route.Stops.Count);
            for (var i = 0; i < route.Stops.Count; i++)
            {
                var leg = i < googleResult.Legs.Count ? googleResult.Legs[i] : null;
                cursor = leg is null ? cursor : cursor.AddMinutes(leg.DurationMins);
                var s = route.Stops[i];
                stops.Add(new Stop(
                    id: s.Id,
                    driverRouteId: s.DriverRouteId,
                    orderIndex: s.OrderIndex,
                    location: s.Location,
                    candidateNodeId: s.CandidateNodeId,
                    estimatedArrival: cursor,
                    pickups: s.Pickups));
            }
            refinedRoutes.Add(new DriverRoute(
                id: route.Id,
                solutionId: route.SolutionId,
                driverId: route.DriverId,
                travelMins: googleResult.TotalDurationMins,
                orderIndex: route.OrderIndex,
                stops: stops));
        }

        return new Solution(
            id: solution.Id,
            optimisationRunId: solution.OptimisationRunId,
            label: solution.Label,
            objective: solution.Objective,
            objectiveTerms: solution.ObjectiveTerms,
            routes: refinedRoutes);
    }
}
