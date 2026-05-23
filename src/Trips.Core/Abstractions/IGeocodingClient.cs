using NetTopologySuite.Geometries;

namespace Trips.Core.Abstractions;

/// <summary>
/// Geocoding client — forward + reverse. Implementation chooses between Google Geocoding and
/// Nominatim (OSM) based on configuration; tests inject a fake.
/// </summary>
public interface IGeocodingClient
{
    /// <summary>Resolve a free-text address to a point. Returns null when nothing plausible matches.</summary>
    Task<GeocodingResult?> GeocodeAsync(string address, CancellationToken ct);

    /// <summary>Resolve a point to a human-readable address. Returns null when no nearby feature exists.</summary>
    Task<GeocodingResult?> ReverseGeocodeAsync(Point point, CancellationToken ct);
}

/// <summary>A geocoded result — formatted address, point, and a coarse precision tier.</summary>
public sealed record GeocodingResult(string FormattedAddress, Point Location, GeocodingPrecision Precision);

public enum GeocodingPrecision
{
    Rooftop = 0,
    Interpolated = 1,
    Approximate = 2,
}
