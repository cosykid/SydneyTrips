using NetTopologySuite.Geometries;
using Trips.Core.Abstractions;

namespace Trips.Api.Stubs;

/// <summary>
/// Stand-in <see cref="IGeocodingClient"/> when WS2 isn't merged in.
/// Returns a deterministic Sydney-ish coordinate for any address; useful for dev/tests.
/// </summary>
internal sealed class StubGeocodingClient : IGeocodingClient
{
    // Sydney CBD ish anchor.
    private static readonly GeometryFactory Factory = new(new PrecisionModel(), 4326);

    public Task<GeocodingResult?> GeocodeAsync(string address, CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(address);
        // Deterministic jitter based on the address hash so multiple calls don't all land on
        // the same coordinate. This is fine for tests; the real client overrides it.
        var hash = address.GetHashCode();
        var dx = ((hash & 0xFF) / 255.0 - 0.5) * 0.05;
        var dy = (((hash >> 8) & 0xFF) / 255.0 - 0.5) * 0.05;
        var point = Factory.CreatePoint(new Coordinate(151.2093 + dx, -33.8688 + dy));
        var result = new GeocodingResult(
            FormattedAddress: address,
            Location: point,
            Precision: GeocodingPrecision.Approximate);
        return Task.FromResult<GeocodingResult?>(result);
    }

    public Task<GeocodingResult?> ReverseGeocodeAsync(Point point, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(point);
        return Task.FromResult<GeocodingResult?>(new GeocodingResult(
            FormattedAddress: $"Stub address near {point.X:F4},{point.Y:F4}",
            Location: point,
            Precision: GeocodingPrecision.Approximate));
    }
}
