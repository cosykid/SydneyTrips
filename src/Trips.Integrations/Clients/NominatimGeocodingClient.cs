using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;
using Trips.Core.Abstractions;

namespace Trips.Integrations.Clients;

/// <summary>
/// Nominatim (OpenStreetMap) geocoding implementation. Free, no key needed, but rate-limited —
/// the OSM usage policy mandates a polite User-Agent and 1 req/sec ceiling. The resilience
/// pipeline handles the rate limit via standard retry; production deployments should
/// front this with a self-hosted Nominatim instance.
/// </summary>
internal sealed class NominatimGeocodingClient : IGeocodingClient
{
    public const string HttpClientName = "NominatimGeocoding";

    private static readonly GeometryFactory GeometryFactory = new(new PrecisionModel(), 4326);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ILogger<NominatimGeocodingClient> _logger;

    public NominatimGeocodingClient(HttpClient http, ILogger<NominatimGeocodingClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<GeocodingResult?> GeocodeAsync(string address, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(address);

        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["GeocodingProvider"] = "nominatim",
            ["Direction"] = "forward",
        });

        _logger.LogInformation("Geocoding via Nominatim");
        var qs = new QueryStringBuilder()
            .Add("q", address)
            .Add("format", "jsonv2")
            .Add("limit", "1")
            .Add("addressdetails", "0")
            .Add("countrycodes", "au")
            .Build();

        using var response = await _http.GetAsync($"/search?{qs}", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var payload = await JsonSerializer.DeserializeAsync<List<NominatimResult>>(stream, JsonOptions, ct).ConfigureAwait(false);

        var first = payload?.FirstOrDefault();
        if (first is null
            || !double.TryParse(first.Lat, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
            || !double.TryParse(first.Lon, NumberStyles.Float, CultureInfo.InvariantCulture, out var lng))
        {
            return null;
        }

        var point = GeometryFactory.CreatePoint(new Coordinate(lng, lat));
        return new GeocodingResult(
            FormattedAddress: first.DisplayName ?? string.Empty,
            Location: point,
            Precision: MapPrecision(first.Type, first.Class_));
    }

    public async Task<GeocodingResult?> ReverseGeocodeAsync(Point point, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(point);

        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["GeocodingProvider"] = "nominatim",
            ["Direction"] = "reverse",
        });

        _logger.LogInformation("Reverse geocoding via Nominatim");
        var qs = new QueryStringBuilder()
            .Add("lat", point.Y.ToString("F6", CultureInfo.InvariantCulture))
            .Add("lon", point.X.ToString("F6", CultureInfo.InvariantCulture))
            .Add("format", "jsonv2")
            .Add("addressdetails", "0")
            .Build();

        using var response = await _http.GetAsync($"/reverse?{qs}", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var payload = await JsonSerializer.DeserializeAsync<NominatimResult>(stream, JsonOptions, ct).ConfigureAwait(false);

        if (payload is null
            || !double.TryParse(payload.Lat, NumberStyles.Float, CultureInfo.InvariantCulture, out var lat)
            || !double.TryParse(payload.Lon, NumberStyles.Float, CultureInfo.InvariantCulture, out var lng))
        {
            return null;
        }

        var resolved = GeometryFactory.CreatePoint(new Coordinate(lng, lat));
        return new GeocodingResult(
            FormattedAddress: payload.DisplayName ?? string.Empty,
            Location: resolved,
            Precision: MapPrecision(payload.Type, payload.Class_));
    }

    private static GeocodingPrecision MapPrecision(string? type, string? klass)
    {
        // Nominatim's "class" + "type" form a tier; "house" / "building" is rooftop-equivalent,
        // "highway" / "road" approximate to interpolated, everything else is approximate.
        if (string.Equals(klass, "building", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "house", StringComparison.OrdinalIgnoreCase))
        {
            return GeocodingPrecision.Rooftop;
        }
        if (string.Equals(klass, "highway", StringComparison.OrdinalIgnoreCase)
            || string.Equals(type, "road", StringComparison.OrdinalIgnoreCase))
        {
            return GeocodingPrecision.Interpolated;
        }
        return GeocodingPrecision.Approximate;
    }

    private sealed class NominatimResult
    {
        // Nominatim uses snake_case, while the standard web JSON convention is camelCase —
        // explicit property names keep the mapping unambiguous.
        [System.Text.Json.Serialization.JsonPropertyName("display_name")]
        public string? DisplayName { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("lat")]
        public string? Lat { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("lon")]
        public string? Lon { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("type")]
        public string? Type { get; set; }

        // "class" is a reserved word in C#; map manually via JSON property name on the property below.
        [System.Text.Json.Serialization.JsonPropertyName("class")]
        public string? Class_ { get; set; }
    }
}
