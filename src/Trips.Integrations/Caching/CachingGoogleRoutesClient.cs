using System.Globalization;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using Trips.Core.Abstractions;
using Trips.Integrations.Configuration;

namespace Trips.Integrations.Caching;

/// <summary>
/// Redis cache decorator for <see cref="IGoogleRoutesClient"/>. Matrices and routes both
/// benefit from caching — matrices because they are called every optimisation run, routes
/// because Google's pricing makes repeat calls expensive.
/// </summary>
/// <remarks>
/// The matrix is cached <em>per origin→destination pair</em> rather than as a whole grid. Google
/// bills per element (origins × destinations), so the goal is to bill each distinct pair at most
/// once: two trips that share a leg (e.g. both run Chatswood→CBD) reuse it, and a re-plan that adds
/// one node only pays for that node's new row and column instead of recomputing the whole matrix.
/// Keys snap coordinates to <see cref="IntegrationCacheOptions.MatrixSnapDecimals"/> so near-identical
/// pickups collapse to one entry, and carry the traffic flag so live-traffic ETAs never collide with
/// (or pollute the long TTL of) the planner's free-flow durations.
/// </remarks>
internal sealed class CachingGoogleRoutesClient : IGoogleRoutesClient
{
    private readonly IGoogleRoutesClient _inner;
    private readonly IIntegrationCache _cache;
    private readonly IntegrationCacheOptions _options;

    public CachingGoogleRoutesClient(
        IGoogleRoutesClient inner,
        IIntegrationCache cache,
        IOptions<IntegrationCacheOptions> options)
    {
        _inner = inner;
        _cache = cache;
        _options = options.Value;
    }

    public async Task<double[,]> ComputeRouteMatrixAsync(
        IReadOnlyList<Point> origins,
        IReadOnlyList<Point> destinations,
        bool trafficAware,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(origins);
        ArgumentNullException.ThrowIfNull(destinations);

        var result = new double[origins.Count, destinations.Count];
        if (origins.Count == 0 || destinations.Count == 0)
        {
            return result;
        }

        var ttl = trafficAware ? _options.TrafficAwareMatrixTtl : _options.RouteMatrixTtl;

        // Sweep the cache one pair at a time, filling what we can and recording the misses per
        // origin row. Sizes here are small (a single trip's node-set), so sequential gets are fine.
        var missesByOrigin = new Dictionary<int, List<int>>();
        for (var i = 0; i < origins.Count; i++)
        {
            for (var j = 0; j < destinations.Count; j++)
            {
                var cached = await _cache.GetAsync<PairValue>(PairKey(origins[i], destinations[j], trafficAware), ct).ConfigureAwait(false);
                if (cached is not null)
                {
                    result[i, j] = cached.Minutes;
                }
                else if (missesByOrigin.TryGetValue(i, out var list))
                {
                    list.Add(j);
                }
                else
                {
                    missesByOrigin[i] = new List<int> { j };
                }
            }
        }

        // Fully served from cache — zero Google elements billed.
        if (missesByOrigin.Count == 0)
        {
            return result;
        }

        // Group origins that miss the *same* set of destinations so each group is a single upstream
        // call billing exactly its missing cells (no over-fetch of already-cached pairs). A cold
        // matrix is one group (everything missing); adding one node to a square matrix is two (the
        // new row, and the new column shared by every pre-existing origin).
        foreach (var group in missesByOrigin.GroupBy(kvp => string.Join(',', kvp.Value)))
        {
            ct.ThrowIfCancellationRequested();
            var originIdx = group.Select(kvp => kvp.Key).ToList();
            var destIdx = group.First().Value; // identical within the group by construction of the key
            var subOrigins = originIdx.Select(i => origins[i]).ToList();
            var subDests = destIdx.Select(j => destinations[j]).ToList();

            var sub = await _inner.ComputeRouteMatrixAsync(subOrigins, subDests, trafficAware, ct).ConfigureAwait(false);

            for (var a = 0; a < originIdx.Count; a++)
            {
                for (var b = 0; b < destIdx.Count; b++)
                {
                    var v = sub[a, b];
                    result[originIdx[a], destIdx[b]] = v;
                    // Only cache routable values. Caching a PositiveInfinity (Google couldn't route
                    // the pair) would pin "unroutable" for the whole TTL even after the data improves.
                    if (double.IsFinite(v))
                    {
                        await _cache.SetAsync(
                            PairKey(origins[originIdx[a]], destinations[destIdx[b]], trafficAware),
                            new PairValue(v),
                            ttl,
                            ct).ConfigureAwait(false);
                    }
                }
            }
        }

        return result;
    }

    /// <summary>
    /// Per-pair cache key: snapped origin + snapped destination + traffic flag. Coordinates are
    /// rounded to <see cref="IntegrationCacheOptions.MatrixSnapDecimals"/> before hashing.
    /// </summary>
    private string PairKey(Point origin, Point dest, bool trafficAware)
    {
        var dp = _options.MatrixSnapDecimals;
        return CacheKey.Build("google:matrixpair",
            Math.Round(origin.Y, dp).ToString(CultureInfo.InvariantCulture),
            Math.Round(origin.X, dp).ToString(CultureInfo.InvariantCulture),
            Math.Round(dest.Y, dp).ToString(CultureInfo.InvariantCulture),
            Math.Round(dest.X, dp).ToString(CultureInfo.InvariantCulture),
            trafficAware ? "t" : "f");
    }

    public async Task<GoogleRoutesResult> ComputeRoutesAsync(
        Point origin,
        Point destination,
        IReadOnlyList<Point> waypoints,
        bool optimizeWaypointOrder,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(waypoints);

        var key = CacheKey.Build("google:routes",
            origin,
            destination,
            string.Join(';', waypoints.Select(p => CacheKey.Build("w", p))),
            optimizeWaypointOrder);

        var cached = await _cache.GetAsync<CachedRoutes>(key, ct).ConfigureAwait(false);
        if (cached is not null)
        {
            return cached.ToDto();
        }

        var fresh = await _inner.ComputeRoutesAsync(origin, destination, waypoints, optimizeWaypointOrder, ct).ConfigureAwait(false);
        await _cache.SetAsync(key, CachedRoutes.From(fresh), _options.ComputeRoutesTtl, ct).ConfigureAwait(false);
        return fresh;
    }

    // ----- Cache shapes -----

    /// <summary>One cached origin→destination driving time, in minutes.</summary>
    private sealed record PairValue(double Minutes);

    private sealed record SerialisedPoint(double Longitude, double Latitude)
    {
        private static readonly GeometryFactory Factory = new(new PrecisionModel(), 4326);
        public static SerialisedPoint From(Point p) => new(p.X, p.Y);
        public Point ToPoint() => Factory.CreatePoint(new Coordinate(Longitude, Latitude));
    }

    private sealed record CachedRouteLeg(SerialisedPoint Start, SerialisedPoint End, double DurationMins, double DistanceMeters)
    {
        public static CachedRouteLeg FromDto(GoogleRouteLeg leg) =>
            new(SerialisedPoint.From(leg.From), SerialisedPoint.From(leg.To), leg.DurationMins, leg.DistanceMeters);

        public GoogleRouteLeg ToDto() => new(Start.ToPoint(), End.ToPoint(), DurationMins, DistanceMeters);
    }

    private sealed record CachedRoutes(List<CachedRouteLeg> Legs, double TotalDurationMins, double TotalDistanceMeters, List<int>? OptimisedWaypointOrder)
    {
        public static CachedRoutes From(GoogleRoutesResult r) =>
            new(
                r.Legs.Select(CachedRouteLeg.FromDto).ToList(),
                r.TotalDurationMins,
                r.TotalDistanceMeters,
                r.OptimisedWaypointOrder?.ToList());

        public GoogleRoutesResult ToDto() =>
            new(
                Legs: Legs.Select(l => l.ToDto()).ToList(),
                TotalDurationMins: TotalDurationMins,
                TotalDistanceMeters: TotalDistanceMeters,
                OptimisedWaypointOrder: OptimisedWaypointOrder);
    }
}
