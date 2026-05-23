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

    /// <summary>TTL for route matrix responses — high cost calls, slow-to-change data.</summary>
    public TimeSpan RouteMatrixTtl { get; set; } = TimeSpan.FromHours(1);

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
