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
    public async Task TfNsw_cached_trip_plan_preserves_leg_geometry_and_stop_names()
    {
        // Regression: the cache used to drop Polyline/FromName/ToName on round-trip, so any cache
        // hit yielded legs with null geometry — which collapsed each candidate node's PT path to
        // null and made the planner map draw crow-fly straight lines instead of the real route.
        var inner = new GeometryBearingTfNsw();
        var cache = new InMemoryIntegrationCache();
        var decorator = new CachingTfNswClient(inner, cache, CacheOptions);

        var origin = Geom.CreatePoint(new Coordinate(151.2073, -33.8730));
        var destination = Geom.CreatePoint(new Coordinate(151.2796, -33.8908));
        var departAt = new DateTimeOffset(2025, 1, 15, 8, 0, 0, TimeSpan.Zero);

        await decorator.TripPlanAsync(origin, destination, departAt, CancellationToken.None);
        var cached = await decorator.TripPlanAsync(origin, destination, departAt, CancellationToken.None);

        inner.Calls.Should().Be(1, "second call should hit the cache");
        var leg = cached.Legs.Should().ContainSingle().Subject;
        leg.FromName.Should().Be("Town Hall");
        leg.ToName.Should().Be("Bondi Junction");
        leg.Polyline.Should().NotBeNull();
        leg.Polyline!.Should().HaveCount(3);
        leg.Polyline![1].X.Should().BeApproximately(151.24, 1e-9);
        leg.Polyline![1].Y.Should().BeApproximately(-33.88, 1e-9);
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

        var first = await decorator.ComputeRouteMatrixAsync(origins, destinations, trafficAware: false, CancellationToken.None);
        var second = await decorator.ComputeRouteMatrixAsync(origins, destinations, trafficAware: false, CancellationToken.None);

        inner.MatrixCalls.Should().Be(1);
        second[0, 0].Should().Be(first[0, 0]);
    }

    [Fact]
    public async Task GoogleRoutes_matrix_reuses_shared_pairs_across_trips()
    {
        // Two different node-sets that happen to share one origin→destination leg. The whole-matrix
        // cache used to miss entirely on the second call (different point set ⇒ different key); the
        // per-pair cache bills the shared leg only once.
        var inner = new CountingGoogleRoutes();
        var cache = new InMemoryIntegrationCache();
        var decorator = new CachingGoogleRoutesClient(inner, cache, CacheOptions);

        var a = Geom.CreatePoint(new Coordinate(151.2073, -33.8730)); // shared origin
        var cbd = Geom.CreatePoint(new Coordinate(151.2073, -33.8688)); // shared destination
        var b = Geom.CreatePoint(new Coordinate(151.7800, -33.7500)); // only in trip 2

        await decorator.ComputeRouteMatrixAsync(new[] { a }, new[] { cbd }, trafficAware: false, CancellationToken.None);
        inner.MatrixElements.Should().Be(1, "cold: the single a→cbd pair is fetched");

        // Trip 2 needs a→cbd (cached) and b→cbd (new). Only the new pair should reach Google.
        await decorator.ComputeRouteMatrixAsync(new[] { a, b }, new[] { cbd }, trafficAware: false, CancellationToken.None);
        inner.MatrixElements.Should().Be(2, "a→cbd is served from cache; only b→cbd is billed");
    }

    [Fact]
    public async Task GoogleRoutes_matrix_replan_only_bills_the_added_node()
    {
        // The #5 case: re-solving after one node is added to a square matrix. A 2×2 matrix grows to
        // 3×3 (9 cells), but 4 are already cached — only the new row + column (5 cells) is billed.
        var inner = new CountingGoogleRoutes();
        var cache = new InMemoryIntegrationCache();
        var decorator = new CachingGoogleRoutesClient(inner, cache, CacheOptions);

        var n0 = Geom.CreatePoint(new Coordinate(151.20, -33.87));
        var n1 = Geom.CreatePoint(new Coordinate(151.25, -33.89));
        var n2 = Geom.CreatePoint(new Coordinate(151.78, -33.75)); // the added node

        var two = new[] { n0, n1 };
        await decorator.ComputeRouteMatrixAsync(two, two, trafficAware: false, CancellationToken.None);
        inner.MatrixElements.Should().Be(4, "cold 2×2");

        inner.MatrixElements = 0;
        var three = new[] { n0, n1, n2 };
        await decorator.ComputeRouteMatrixAsync(three, three, trafficAware: false, CancellationToken.None);
        inner.MatrixElements.Should().Be(5, "only n2's row (3) + column (2) are new; the original 4 are cached");
    }

    [Fact]
    public async Task GoogleRoutes_matrix_separates_traffic_aware_from_free_flow()
    {
        // Live-ETA (traffic-aware) and planner (free-flow) durations for the same pair must not
        // collide — they're different SKUs with different freshness, so they cache independently.
        var inner = new CountingGoogleRoutes();
        var cache = new InMemoryIntegrationCache();
        var decorator = new CachingGoogleRoutesClient(inner, cache, CacheOptions);

        var origins = new[] { Geom.CreatePoint(new Coordinate(151.2073, -33.8730)) };
        var destinations = new[] { Geom.CreatePoint(new Coordinate(151.2796, -33.8908)) };

        await decorator.ComputeRouteMatrixAsync(origins, destinations, trafficAware: false, CancellationToken.None);
        await decorator.ComputeRouteMatrixAsync(origins, destinations, trafficAware: true, CancellationToken.None);

        inner.MatrixCalls.Should().Be(2, "traffic-aware and free-flow are keyed separately");
        inner.LastTrafficAware.Should().BeTrue();
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

    /// <summary>Returns a single train leg carrying a polyline and stop names, so the cache
    /// round-trip of <see cref="TfNswJourneyLeg.Polyline"/>/<c>FromName</c>/<c>ToName</c> is exercised.</summary>
    private sealed class GeometryBearingTfNsw : ITfNswClient
    {
        public int Calls;

        public Task<TfNswTripPlan> TripPlanAsync(Point origin, Point destination, DateTimeOffset departAt, CancellationToken ct)
        {
            Calls++;
            var polyline = new[]
            {
                Geom.CreatePoint(new Coordinate(151.2069, -33.8743)),
                Geom.CreatePoint(new Coordinate(151.24, -33.88)),
                Geom.CreatePoint(new Coordinate(151.2503, -33.8918)),
            };
            var leg = new TfNswJourneyLeg("train", 12, origin, destination, "T4",
                FromName: "Town Hall", ToName: "Bondi Junction", Polyline: polyline);
            return Task.FromResult(new TfNswTripPlan(new[] { leg }, 0, 12));
        }

        public Task<IReadOnlyList<TfNswCoordinateStop>> CoordinateRequestAsync(Point origin, int radiusMeters, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TfNswCoordinateStop>>(Array.Empty<TfNswCoordinateStop>());

        public Task<IReadOnlyList<TfNswDeparture>> DepartureAsync(string stopId, DateTimeOffset @from, CancellationToken ct)
            => Task.FromResult<IReadOnlyList<TfNswDeparture>>(Array.Empty<TfNswDeparture>());

        public async IAsyncEnumerable<TfNswGtfsTripUpdate> GtfsRtTripUpdatesAsync(string mode, [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class CountingGoogleRoutes : IGoogleRoutesClient
    {
        public int MatrixCalls;
        public int MatrixElements; // origins × destinations actually forwarded upstream (= billed)
        public bool? LastTrafficAware;
        public int RoutesCalls;

        public Task<double[,]> ComputeRouteMatrixAsync(IReadOnlyList<Point> origins, IReadOnlyList<Point> destinations, bool trafficAware, CancellationToken ct)
        {
            MatrixCalls++;
            MatrixElements += origins.Count * destinations.Count;
            LastTrafficAware = trafficAware;
            // Deterministic per-pair value keyed on coordinates so a pair returns the same minutes
            // regardless of which call/sub-grid it arrives in — lets tests assert reuse correctness.
            var m = new double[origins.Count, destinations.Count];
            for (int i = 0; i < origins.Count; i++)
                for (int j = 0; j < destinations.Count; j++)
                    m[i, j] = Math.Abs(origins[i].X - destinations[j].X) + Math.Abs(origins[i].Y - destinations[j].Y) + 1;
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
