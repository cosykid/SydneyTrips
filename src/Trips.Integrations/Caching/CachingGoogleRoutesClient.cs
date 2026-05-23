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
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(origins);
        ArgumentNullException.ThrowIfNull(destinations);

        var key = CacheKey.Build("google:matrix",
            origins.Count,
            destinations.Count,
            string.Join(';', origins.Select(p => CacheKey.Build("o", p))),
            string.Join(';', destinations.Select(p => CacheKey.Build("d", p))));

        var cached = await _cache.GetAsync<CachedMatrix>(key, ct).ConfigureAwait(false);
        if (cached is not null && cached.Rows == origins.Count && cached.Cols == destinations.Count)
        {
            return cached.ToMatrix();
        }

        var fresh = await _inner.ComputeRouteMatrixAsync(origins, destinations, ct).ConfigureAwait(false);
        await _cache.SetAsync(key, CachedMatrix.From(fresh), _options.RouteMatrixTtl, ct).ConfigureAwait(false);
        return fresh;
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

    private sealed record CachedMatrix(int Rows, int Cols, double[] Flat)
    {
        public static CachedMatrix From(double[,] m)
        {
            var rows = m.GetLength(0);
            var cols = m.GetLength(1);
            var flat = new double[rows * cols];
            for (int i = 0; i < rows; i++)
            {
                for (int j = 0; j < cols; j++)
                {
                    flat[i * cols + j] = m[i, j];
                }
            }
            return new CachedMatrix(rows, cols, flat);
        }

        public double[,] ToMatrix()
        {
            var m = new double[Rows, Cols];
            for (int i = 0; i < Rows; i++)
            {
                for (int j = 0; j < Cols; j++)
                {
                    m[i, j] = Flat[i * Cols + j];
                }
            }
            return m;
        }
    }

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
