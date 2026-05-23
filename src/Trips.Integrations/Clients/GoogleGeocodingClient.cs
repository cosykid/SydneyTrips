using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;
using Trips.Core.Abstractions;

namespace Trips.Integrations.Clients;

/// <summary>
/// Google-backed geocoding implementation. Forward + reverse via maps.googleapis.com/maps/api/geocode/json.
/// </summary>
internal sealed class GoogleGeocodingClient : IGeocodingClient
{
    public const string HttpClientName = "GoogleGeocoding";

    private static readonly GeometryFactory GeometryFactory = new(new PrecisionModel(), 4326);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ILogger<GoogleGeocodingClient> _logger;

    public GoogleGeocodingClient(HttpClient http, ILogger<GoogleGeocodingClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<GeocodingResult?> GeocodeAsync(string address, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(address);

        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["GeocodingProvider"] = "google",
            ["Direction"] = "forward",
        });

        _logger.LogInformation("Geocoding via Google");
        var qs = new QueryStringBuilder()
            .Add("address", address)
            .Build();

        return await CallAndMapAsync($"/maps/api/geocode/json?{qs}", ct).ConfigureAwait(false);
    }

    public async Task<GeocodingResult?> ReverseGeocodeAsync(Point point, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(point);

        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["GeocodingProvider"] = "google",
            ["Direction"] = "reverse",
        });

        _logger.LogInformation("Reverse geocoding via Google");
        var qs = new QueryStringBuilder()
            .Add("latlng", $"{point.Y.ToString("F6", CultureInfo.InvariantCulture)},{point.X.ToString("F6", CultureInfo.InvariantCulture)}")
            .Build();

        return await CallAndMapAsync($"/maps/api/geocode/json?{qs}", ct).ConfigureAwait(false);
    }

    private async Task<GeocodingResult?> CallAndMapAsync(string path, CancellationToken ct)
    {
        using var response = await _http.GetAsync(path, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var payload = await JsonSerializer.DeserializeAsync<GeocodeResponse>(stream, JsonOptions, ct).ConfigureAwait(false);

        var first = payload?.Results?.FirstOrDefault();
        if (first?.Geometry?.Location is null)
        {
            return null;
        }

        var loc = first.Geometry.Location;
        var point = GeometryFactory.CreatePoint(new Coordinate(loc.Lng, loc.Lat));
        var precision = MapPrecision(first.Geometry.LocationType);
        return new GeocodingResult(first.FormattedAddress ?? string.Empty, point, precision);
    }

    private static GeocodingPrecision MapPrecision(string? locationType) => locationType switch
    {
        "ROOFTOP" => GeocodingPrecision.Rooftop,
        "RANGE_INTERPOLATED" => GeocodingPrecision.Interpolated,
        "GEOMETRIC_CENTER" => GeocodingPrecision.Interpolated,
        _ => GeocodingPrecision.Approximate,
    };

    private sealed class GeocodeResponse
    {
        public List<GeocodeResult>? Results { get; set; }
        public string? Status { get; set; }
    }

    private sealed class GeocodeResult
    {
        public string? FormattedAddress { get; set; }
        public GeometryDto? Geometry { get; set; }
    }

    private sealed class GeometryDto
    {
        public LocationDto? Location { get; set; }
        public string? LocationType { get; set; }
    }

    private sealed class LocationDto
    {
        public double Lat { get; set; }
        public double Lng { get; set; }
    }
}
