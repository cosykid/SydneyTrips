namespace Trips.Integrations.Configuration;

/// <summary>
/// Configuration for the TfNSW Open Data API client. Bound from <c>Integrations:TfNsw</c>.
/// </summary>
public sealed class TfNswOptions
{
    public const string SectionName = "Integrations:TfNsw";

    /// <summary>Root URL — defaults to the production endpoint, overridden in tests for mock servers.</summary>
    public string BaseUrl { get; set; } = "https://api.transport.nsw.gov.au";

    /// <summary>API key issued by opendata.transport.nsw.gov.au. Sent as <c>Authorization: apikey &lt;key&gt;</c>.</summary>
    public string ApiKey { get; set; } = string.Empty;

    /// <summary>
    /// GTFS-Realtime feed paths keyed by the <c>mode</c> argument of <c>GtfsRtTripUpdatesAsync</c>.
    /// Defaults cover the four big Sydney feeds.
    /// </summary>
    public IDictionary<string, string> GtfsRealtimeFeeds { get; set; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["trains"] = "/v1/gtfs/realtime/sydneytrains",
        ["buses"] = "/v1/gtfs/realtime/buses",
        ["ferries"] = "/v1/gtfs/realtime/ferries",
        ["lightrail"] = "/v1/gtfs/realtime/lightrail",
    };

    /// <summary>Outbound request timeout in seconds applied by the resilience pipeline.</summary>
    public int RequestTimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// Configuration for the Google Routes API client. Bound from <c>Integrations:Google</c>.
/// </summary>
public sealed class GoogleRoutesOptions
{
    public const string SectionName = "Integrations:Google";

    public string BaseUrl { get; set; } = "https://routes.googleapis.com";

    /// <summary>Geocoding service base URL (Google's geocoding is hosted on maps.googleapis.com, not routes).</summary>
    public string GeocodingBaseUrl { get; set; } = "https://maps.googleapis.com";

    /// <summary>API key sent as <c>X-Goog-Api-Key</c>.</summary>
    public string ApiKey { get; set; } = string.Empty;

    public int RequestTimeoutSeconds { get; set; } = 30;
}

/// <summary>
/// Configuration for a self-hosted OSRM (Open Source Routing Machine) instance used for the
/// <em>planning</em> travel-time matrix. Bound from <c>Integrations:Osrm</c>.
/// </summary>
/// <remarks>
/// OSRM serves free-flow driving durations from OpenStreetMap data at zero marginal cost — exactly
/// what the planner needs (it solves against a future departure, so live traffic is noise). When a
/// <see cref="BaseUrl"/> is configured the free-flow matrix is routed here instead of Google's
/// per-element Route Matrix; Google is then reserved for the live, traffic-aware ETA path only.
/// Leave <see cref="BaseUrl"/> empty to disable OSRM and route everything through Google (the
/// zero-ops default). See <c>docs/operations-cost.md</c> for the one-time data-prep steps.
/// </remarks>
public sealed class OsrmOptions
{
    public const string SectionName = "Integrations:Osrm";

    /// <summary>
    /// Root URL of the <c>osrm-routed</c> HTTP server (e.g. <c>http://localhost:5001</c>). Empty
    /// disables OSRM — the planner then falls back to Google's matrix, as before.
    /// </summary>
    public string BaseUrl { get; set; } = string.Empty;

    /// <summary>Outbound request timeout in seconds.</summary>
    public int RequestTimeoutSeconds { get; set; } = 15;

    /// <summary>True when a base URL is configured and OSRM should be wired in.</summary>
    public bool Enabled => !string.IsNullOrWhiteSpace(BaseUrl);
}

/// <summary>
/// Configuration for the geocoding client. Bound from <c>Integrations:Geocoding</c>.
/// </summary>
public sealed class GeocodingOptions
{
    public const string SectionName = "Integrations:Geocoding";

    /// <summary>Which provider to wire up. Falls back to Nominatim when not set, since Nominatim needs no key.</summary>
    public GeocodingProvider Provider { get; set; } = GeocodingProvider.Nominatim;

    /// <summary>Nominatim endpoint — defaults to the public OSM instance; override for a self-hosted Nominatim.</summary>
    public string NominatimBaseUrl { get; set; } = "https://nominatim.openstreetmap.org";

    /// <summary>
    /// Polite User-Agent identifying this client to Nominatim. Required by the OSM usage policy.
    /// </summary>
    public string NominatimUserAgent { get; set; } = "SydneyTrips/1.0 (https://github.com/sydneytrips)";

    public int RequestTimeoutSeconds { get; set; } = 30;
}

public enum GeocodingProvider
{
    Nominatim = 0,
    Google = 1,
}

/// <summary>
/// Redis-backed cache options for the integration decorators. Bound from <c>Integrations:Cache</c>.
/// </summary>
public sealed class IntegrationCacheOptions
{
    public const string SectionName = "Integrations:Cache";

    /// <summary>Redis connection string. When null/empty the cache decorators are no-ops.</summary>
    public string? RedisConnectionString { get; set; }

    /// <summary>Prefix on every Redis key so multiple environments can share an instance.</summary>
    public string KeyPrefix { get; set; } = "trips:integrations:";

    /// <summary>
    /// TTL for free-flow (traffic-unaware) route matrix pairs — the planning path. These are
    /// stable geography (the driving time A→B only changes when roads change), so we keep them for
    /// a long time: every reuse is one fewer billed Google element.
    /// </summary>
    public TimeSpan RouteMatrixTtl { get; set; } = TimeSpan.FromDays(14);

    /// <summary>
    /// TTL for traffic-aware route matrix pairs — the live ETA path. Short, because the whole point
    /// of asking for traffic is that it changes minute to minute; a long TTL would serve stale ETAs.
    /// </summary>
    public TimeSpan TrafficAwareMatrixTtl { get; set; } = TimeSpan.FromMinutes(1);

    /// <summary>
    /// Decimal places coordinates are rounded to when building a per-pair matrix cache key.
    /// 4 dp ≈ 11 m — below what the router can resolve, so two near-identical pickups collapse to
    /// one cache entry, sharply raising the hit rate without changing any returned duration.
    /// </summary>
    public int MatrixSnapDecimals { get; set; } = 4;

    /// <summary>TTL for compute-routes calls.</summary>
    public TimeSpan ComputeRoutesTtl { get; set; } = TimeSpan.FromHours(1);

    /// <summary>TTL for geocoding (address → point and back). Addresses are stable for weeks.</summary>
    public TimeSpan GeocodeTtl { get; set; } = TimeSpan.FromDays(30);

    /// <summary>TTL for TfNSW trip plans. Public-transport timetables change slowly within the day.</summary>
    public TimeSpan TripPlanTtl { get; set; } = TimeSpan.FromMinutes(5);

    /// <summary>TTL for coordinate-request (nearby stops). Stop locations are essentially static.</summary>
    public TimeSpan CoordinateRequestTtl { get; set; } = TimeSpan.FromDays(1);

    /// <summary>TTL for live departures. Short — these refresh fast.</summary>
    public TimeSpan DepartureTtl { get; set; } = TimeSpan.FromSeconds(30);
}
