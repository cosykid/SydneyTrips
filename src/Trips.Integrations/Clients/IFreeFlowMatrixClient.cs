using NetTopologySuite.Geometries;

namespace Trips.Integrations.Clients;

/// <summary>
/// A source of <em>free-flow</em> driving-time matrices — durations that ignore live traffic.
/// Implemented by <see cref="OsrmRoutesClient"/> (self-hosted, zero marginal cost) and consumed by
/// <see cref="HybridRoutesClient"/>, which routes the planner's matrix here instead of Google's
/// per-element Route Matrix. There is deliberately no traffic flag: free-flow is the whole contract.
/// </summary>
internal interface IFreeFlowMatrixClient
{
    /// <summary>
    /// Driving-time matrix in minutes between every origin and every destination, free-flow.
    /// <c>result[i, j]</c> is origin <c>i</c> → destination <c>j</c>; an unroutable pair is
    /// <see cref="double.PositiveInfinity"/> (matching <see cref="IGoogleRoutesClient"/>'s shape so
    /// the runner's "merge finite values over haversine" logic is identical for either source).
    /// </summary>
    Task<double[,]> ComputeMatrixAsync(IReadOnlyList<Point> origins, IReadOnlyList<Point> destinations, CancellationToken ct);
}
