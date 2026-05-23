using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using Trips.Core.Abstractions;
using Trips.Integrations.Configuration;

namespace Trips.Integrations.Caching;

/// <summary>
/// Redis cache decorator for <see cref="IGeocodingClient"/>. Geocodes have a long TTL —
/// the underlying mapping changes on geological timescales.
/// </summary>
internal sealed class CachingGeocodingClient : IGeocodingClient
{
    private readonly IGeocodingClient _inner;
    private readonly IIntegrationCache _cache;
    private readonly IntegrationCacheOptions _options;

    public CachingGeocodingClient(
        IGeocodingClient inner,
        IIntegrationCache cache,
        IOptions<IntegrationCacheOptions> options)
    {
        _inner = inner;
        _cache = cache;
        _options = options.Value;
    }

    public async Task<GeocodingResult?> GeocodeAsync(string address, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(address);

        var key = CacheKey.Build("geocode:fwd", address.Trim().ToLowerInvariant());
        var cached = await _cache.GetAsync<CachedGeocodingResult>(key, ct).ConfigureAwait(false);
        if (cached is not null)
        {
            return cached.ToDto();
        }

        var fresh = await _inner.GeocodeAsync(address, ct).ConfigureAwait(false);
        if (fresh is not null)
        {
            await _cache.SetAsync(key, CachedGeocodingResult.From(fresh), _options.GeocodeTtl, ct).ConfigureAwait(false);
        }
        return fresh;
    }

    public async Task<GeocodingResult?> ReverseGeocodeAsync(Point point, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(point);

        var key = CacheKey.Build("geocode:rev", point);
        var cached = await _cache.GetAsync<CachedGeocodingResult>(key, ct).ConfigureAwait(false);
        if (cached is not null)
        {
            return cached.ToDto();
        }

        var fresh = await _inner.ReverseGeocodeAsync(point, ct).ConfigureAwait(false);
        if (fresh is not null)
        {
            await _cache.SetAsync(key, CachedGeocodingResult.From(fresh), _options.GeocodeTtl, ct).ConfigureAwait(false);
        }
        return fresh;
    }

    private sealed record CachedGeocodingResult(string FormattedAddress, double Longitude, double Latitude, GeocodingPrecision Precision)
    {
        private static readonly GeometryFactory Factory = new(new PrecisionModel(), 4326);

        public static CachedGeocodingResult From(GeocodingResult r) =>
            new(r.FormattedAddress, r.Location.X, r.Location.Y, r.Precision);

        public GeocodingResult ToDto() =>
            new(FormattedAddress, Factory.CreatePoint(new Coordinate(Longitude, Latitude)), Precision);
    }
}
