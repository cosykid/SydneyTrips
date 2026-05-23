using FluentAssertions;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using Trips.Core.Abstractions;
using Trips.Integrations.Caching;
using Trips.Integrations.Configuration;

namespace Trips.Integrations.Tests;

/// <summary>
/// Verifies the cache decorators stop forwarding to the upstream once a result is cached,
/// and that the cached payload deserialises identically.
/// </summary>
public sealed class CachingDecoratorTests
{
    private static readonly GeometryFactory Geom = new(new PrecisionModel(), 4326);
    private static readonly IOptions<IntegrationCacheOptions> CacheOptions =
        Options.Create(new IntegrationCacheOptions
        {
            RouteMatrixTtl = TimeSpan.FromMinutes(5),
            ComputeRoutesTtl = TimeSpan.FromMinutes(5),
            GeocodeTtl = TimeSpan.FromMinutes(5),
            TripPlanTtl = TimeSpan.FromMinutes(5),
            CoordinateRequestTtl = TimeSpan.FromMinutes(5),
            DepartureTtl = TimeSpan.FromMinutes(5),
        });

    [Fact]
    public async Task TfNsw_trip_plan_serves_second_call_from_cache()
    {
        var inner = new CountingTfNsw();
        var cache = new InMemoryIntegrationCache();
        var decorator = new CachingTfNswClient(inner, cache, CacheOptions);

        var origin = Geom.CreatePoint(new Coordinate(151.2073, -33.8730));
        var destination = Geom.CreatePoint(new Coordinate(151.2796, -33.8908));
        var departAt = new DateTimeOffset(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);

        var first = await decorator.TripPlanAsync(origin, destination, departAt, CancellationToken.None);
        var second = await decorator.TripPlanAsync(origin, destination, departAt, CancellationToken.None);

        inner.TripPlanCalls.Should().Be(1, "second call should hit the cache");
        first.Legs.Should().HaveCount(second.Legs.Count);
        second.Legs[0].Mode.Should().Be(first.Legs[0].Mode);
        second.Legs[0].From.X.Should().Be(first.Legs[0].From.X);
    }

    [Fact]
    public async Task TfNsw_coordinate_request_caches_by_origin_and_radius()
    {
        var inner = new CountingTfNsw();
        var cache = new InMemoryIntegrationCache();
        var decorator = new CachingTfNswClient(inner, cache, CacheOptions);

        var origin = Geom.CreatePoint(new Coordinate(151.207, -33.873));
        await decorator.CoordinateRequestAsync(origin, 800, CancellationToken.None);
        await decorator.CoordinateRequestAsync(origin, 800, CancellationToken.None);
        await decorator.CoordinateRequestAsync(origin, 1200, CancellationToken.None);

        inner.CoordCalls.Should().Be(2, "same coord+radius cached; different radius bypasses cache");
    }

    [Fact]
    public async Task GoogleRoutes_matrix_is_cached_by_payload()
    {
        var inner = new CountingGoogleRoutes();
        var cache = new InMemoryIntegrationCache();
        var decorator = new CachingGoogleRoutesClient(inner, cache, CacheOptions);

        var origins = new[] { Geom.CreatePoint(new Coordinate(151.2073, -33.8730)) };
        var destinations = new[] { Geom.CreatePoint(new Coordinate(151.2796, -33.8908)) };

        var first = await decorator.ComputeRouteMatrixAsync(origins, destinations, CancellationToken.None);
        var second = await decorator.ComputeRouteMatrixAsync(origins, destinations, CancellationToken.None);

        inner.MatrixCalls.Should().Be(1);
        second[0, 0].Should().Be(first[0, 0]);
    }

    [Fact]
    public async Task GoogleRoutes_compute_routes_is_cached_separately_from_matrix()
    {
        var inner = new CountingGoogleRoutes();
        var cache = new InMemoryIntegrationCache();
        var decorator = new CachingGoogleRoutesClient(inner, cache, CacheOptions);

        var origin = Geom.CreatePoint(new Coordinate(151.2073, -33.8730));
        var destination = Geom.CreatePoint(new Coordinate(151.2796, -33.8908));
        var waypoints = new[] { Geom.CreatePoint(new Coordinate(151.2509, -33.8919)) };

        await decorator.ComputeRoutesAsync(origin, destination, waypoints, true, CancellationToken.None);
        await decorator.ComputeRoutesAsync(origin, destination, waypoints, true, CancellationToken.None);
        await decorator.ComputeRoutesAsync(origin, destination, waypoints, false, CancellationToken.None);

        inner.RoutesCalls.Should().Be(2, "optimize flag is part of the cache key");
    }

    [Fact]
    public async Task Geocoding_decorator_caches_by_normalised_address()
    {
        var inner = new CountingGeocoder();
        var cache = new InMemoryIntegrationCache();
        var decorator = new CachingGeocodingClient(inner, cache, CacheOptions);

        await decorator.GeocodeAsync("Bondi Beach NSW", CancellationToken.None);
        await decorator.GeocodeAsync("bondi beach nsw", CancellationToken.None);
        await decorator.GeocodeAsync("  Bondi Beach NSW  ", CancellationToken.None);

        inner.ForwardCalls.Should().Be(1, "addresses normalise to the same cache key");
    }

    [Fact]
    public async Task Geocoding_decorator_does_not_cache_null_results()
    {
        var inner = new CountingGeocoder { ReturnNull = true };
        var cache = new InMemoryIntegrationCache();
        var decorator = new CachingGeocodingClient(inner, cache, CacheOptions);

        await decorator.GeocodeAsync("Nowhere", CancellationToken.None);
        await decorator.GeocodeAsync("Nowhere", CancellationToken.None);

        inner.ForwardCalls.Should().Be(2);
    }

    // ----- Test doubles -----

    private sealed class CountingTfNsw : ITfNswClient
    {
        public int TripPlanCalls;
        public int CoordCalls;
        public int DepartureCalls;

        public Task<TfNswTripPlan> TripPlanAsync(Point origin, Point destination, DateTimeOffset departAt, CancellationToken ct)
        {
            TripPlanCalls++;
            var leg = new TfNswJourneyLeg("train", 12, origin, destination, "T4");
            return Task.FromResult(new TfNswTripPlan(new[] { leg }, 2, 12));
        }

        public Task<IReadOnlyList<TfNswCoordinateStop>> CoordinateRequestAsync(Point origin, int radiusMeters, CancellationToken ct)
        {
            CoordCalls++;
            return Task.FromResult<IReadOnlyList<TfNswCoordinateStop>>(new[]
            {
                new TfNswCoordinateStop("stop1", "Stop 1", origin, 50, "train"),
            });
        }

        public Task<IReadOnlyList<TfNswDeparture>> DepartureAsync(string stopId, DateTimeOffset @from, CancellationToken ct)
        {
            DepartureCalls++;
            return Task.FromResult<IReadOnlyList<TfNswDeparture>>(new[]
            {
                new TfNswDeparture(stopId, "T4", @from, @from.AddMinutes(1), "VEH-1"),
            });
        }

        public async IAsyncEnumerable<TfNswGtfsTripUpdate> GtfsRtTripUpdatesAsync(string mode, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class CountingGoogleRoutes : IGoogleRoutesClient
    {
        public int MatrixCalls;
        public int RoutesCalls;

        public Task<double[,]> ComputeRouteMatrixAsync(IReadOnlyList<Point> origins, IReadOnlyList<Point> destinations, CancellationToken ct)
        {
            MatrixCalls++;
            var m = new double[origins.Count, destinations.Count];
            for (int i = 0; i < origins.Count; i++)
                for (int j = 0; j < destinations.Count; j++)
                    m[i, j] = 10 + i + j;
            return Task.FromResult(m);
        }

        public Task<GoogleRoutesResult> ComputeRoutesAsync(Point origin, Point destination, IReadOnlyList<Point> waypoints, bool optimizeWaypointOrder, CancellationToken ct)
        {
            RoutesCalls++;
            var legs = new[] { new GoogleRouteLeg(origin, destination, 30, 5000) };
            return Task.FromResult(new GoogleRoutesResult(legs, 30, 5000, optimizeWaypointOrder ? new[] { 0 } : null));
        }
    }

    private sealed class CountingGeocoder : IGeocodingClient
    {
        public int ForwardCalls;
        public int ReverseCalls;
        public bool ReturnNull;

        public Task<GeocodingResult?> GeocodeAsync(string address, CancellationToken ct)
        {
            ForwardCalls++;
            if (ReturnNull)
            {
                return Task.FromResult<GeocodingResult?>(null);
            }
            var loc = Geom.CreatePoint(new Coordinate(151.0, -33.0));
            return Task.FromResult<GeocodingResult?>(new GeocodingResult(address, loc, GeocodingPrecision.Rooftop));
        }

        public Task<GeocodingResult?> ReverseGeocodeAsync(Point point, CancellationToken ct)
        {
            ReverseCalls++;
            return Task.FromResult<GeocodingResult?>(new GeocodingResult("somewhere", point, GeocodingPrecision.Approximate));
        }
    }

    private sealed class InMemoryIntegrationCache : IIntegrationCache
    {
        private readonly Dictionary<string, string> _store = new();

        public Task<T?> GetAsync<T>(string key, CancellationToken ct) where T : class
        {
            if (_store.TryGetValue(key, out var json))
            {
                return Task.FromResult(System.Text.Json.JsonSerializer.Deserialize<T>(json));
            }
            return Task.FromResult<T?>(null);
        }

        public Task SetAsync<T>(string key, T value, TimeSpan ttl, CancellationToken ct) where T : class
        {
            _store[key] = System.Text.Json.JsonSerializer.Serialize(value);
            return Task.CompletedTask;
        }
    }
}
