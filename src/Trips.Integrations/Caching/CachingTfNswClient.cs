using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using Trips.Core.Abstractions;
using Trips.Integrations.Configuration;

namespace Trips.Integrations.Caching;

/// <summary>
/// Redis cache decorator for <see cref="ITfNswClient"/>. Wraps the live implementation
/// and caches every read except the GTFS-RT stream (streams cannot be cached safely).
/// </summary>
internal sealed class CachingTfNswClient : ITfNswClient
{
    private readonly ITfNswClient _inner;
    private readonly IIntegrationCache _cache;
    private readonly IntegrationCacheOptions _options;

    public CachingTfNswClient(
        ITfNswClient inner,
        IIntegrationCache cache,
        IOptions<IntegrationCacheOptions> options)
    {
        _inner = inner;
        _cache = cache;
        _options = options.Value;
    }

    public async Task<TfNswTripPlan> TripPlanAsync(Point origin, Point destination, DateTimeOffset departAt, CancellationToken ct)
    {
        // Bucket the departure to whole minutes so two near-identical calls share a cache slot.
        var bucket = new DateTimeOffset(departAt.Year, departAt.Month, departAt.Day, departAt.Hour, departAt.Minute, 0, departAt.Offset);
        var key = CacheKey.Build("tfnsw:trip", origin, destination, bucket);

        var cached = await _cache.GetAsync<CachedTripPlan>(key, ct).ConfigureAwait(false);
        if (cached is not null)
        {
            return cached.ToDto();
        }

        var fresh = await _inner.TripPlanAsync(origin, destination, departAt, ct).ConfigureAwait(false);
        await _cache.SetAsync(key, CachedTripPlan.From(fresh), _options.TripPlanTtl, ct).ConfigureAwait(false);
        return fresh;
    }

    public async Task<IReadOnlyList<TfNswCoordinateStop>> CoordinateRequestAsync(Point origin, int radiusMeters, CancellationToken ct)
    {
        var key = CacheKey.Build("tfnsw:coord", origin, radiusMeters);
        var cached = await _cache.GetAsync<CachedCoordResponse>(key, ct).ConfigureAwait(false);
        if (cached is not null)
        {
            return cached.Stops.Select(s => s.ToDto()).ToList();
        }

        var fresh = await _inner.CoordinateRequestAsync(origin, radiusMeters, ct).ConfigureAwait(false);
        await _cache.SetAsync(key, CachedCoordResponse.From(fresh), _options.CoordinateRequestTtl, ct).ConfigureAwait(false);
        return fresh;
    }

    public async Task<IReadOnlyList<TfNswDeparture>> DepartureAsync(string stopId, DateTimeOffset @from, CancellationToken ct)
    {
        // 30-second bucket — TfNSW departure data refreshes about that fast.
        var bucket = DateTimeOffset.FromUnixTimeSeconds(@from.ToUnixTimeSeconds() / 30 * 30);
        var key = CacheKey.Build("tfnsw:dep", stopId, bucket);
        var cached = await _cache.GetAsync<CachedDepartureResponse>(key, ct).ConfigureAwait(false);
        if (cached is not null)
        {
            return cached.Departures.ToList();
        }

        var fresh = await _inner.DepartureAsync(stopId, @from, ct).ConfigureAwait(false);
        await _cache.SetAsync(key, CachedDepartureResponse.From(fresh), _options.DepartureTtl, ct).ConfigureAwait(false);
        return fresh;
    }

    public IAsyncEnumerable<TfNswGtfsTripUpdate> GtfsRtTripUpdatesAsync(string mode, CancellationToken ct) =>
        _inner.GtfsRtTripUpdatesAsync(mode, ct);

    // ----- Cache shapes: JSON-friendly, decoupled from NTS Point (which doesn't serialise cleanly) -----

    private sealed record SerialisedPoint(double Longitude, double Latitude)
    {
        private static readonly GeometryFactory Factory = new(new PrecisionModel(), 4326);

        public static SerialisedPoint From(Point p) => new(p.X, p.Y);

        public Point ToPoint() => Factory.CreatePoint(new Coordinate(Longitude, Latitude));
    }

    private sealed record CachedJourneyLeg(
        string Mode,
        int DurationMins,
        SerialisedPoint Start,
        SerialisedPoint End,
        string? RouteShortName,
        string? FromName,
        string? ToName,
        List<SerialisedPoint>? Polyline)
    {
        public static CachedJourneyLeg FromDto(TfNswJourneyLeg leg) =>
            new(
                leg.Mode,
                leg.DurationMins,
                SerialisedPoint.From(leg.From),
                SerialisedPoint.From(leg.To),
                leg.RouteShortName,
                leg.FromName,
                leg.ToName,
                // Round-trip the leg geometry and stop names through the cache. Dropping them (the
                // original bug) meant any cache hit yielded legs with a null Polyline, which
                // collapsed each candidate node's PT path to null and made the map draw crow-fly
                // straight lines instead of the real route.
                leg.Polyline?.Select(SerialisedPoint.From).ToList());

        public TfNswJourneyLeg ToDto() =>
            new(
                Mode,
                DurationMins,
                Start.ToPoint(),
                End.ToPoint(),
                RouteShortName,
                FromName,
                ToName,
                Polyline?.Select(p => p.ToPoint()).ToList());
    }

    private sealed record CachedTripPlan(List<CachedJourneyLeg> Legs, int TotalWalkMins, int TotalPtMins)
    {
        public static CachedTripPlan From(TfNswTripPlan plan) =>
            new(plan.Legs.Select(CachedJourneyLeg.FromDto).ToList(), plan.TotalWalkMins, plan.TotalPtMins);

        public TfNswTripPlan ToDto() =>
            new(Legs.Select(l => l.ToDto()).ToList(), TotalWalkMins, TotalPtMins);
    }

    private sealed record CachedCoordStop(string StopId, string Name, SerialisedPoint Location, int DistanceMeters, string Mode)
    {
        public static CachedCoordStop From(TfNswCoordinateStop s) =>
            new(s.StopId, s.Name, SerialisedPoint.From(s.Location), s.DistanceMeters, s.Mode);

        public TfNswCoordinateStop ToDto() =>
            new(StopId, Name, Location.ToPoint(), DistanceMeters, Mode);
    }

    private sealed record CachedCoordResponse(List<CachedCoordStop> Stops)
    {
        public static CachedCoordResponse From(IReadOnlyList<TfNswCoordinateStop> stops) =>
            new(stops.Select(CachedCoordStop.From).ToList());
    }

    private sealed record CachedDepartureResponse(List<TfNswDeparture> Departures)
    {
        public static CachedDepartureResponse From(IReadOnlyList<TfNswDeparture> deps) =>
            new(deps.ToList());
    }
}
