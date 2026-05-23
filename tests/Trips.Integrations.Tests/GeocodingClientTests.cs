using FluentAssertions;
using NetTopologySuite.Geometries;

namespace Trips.Integrations.Tests;

[Collection(MockServerCollection.Name)]
public sealed class GeocodingClientTests : MockServerTestBase
{
    private static readonly GeometryFactory Geom = new(new PrecisionModel(), 4326);

    public GeocodingClientTests(MockServerFixture fixture) : base(fixture) { }

    [Fact]
    public async Task Nominatim_GeocodeAsync_returns_point_from_search_response()
    {
        var client = ClientFactory.Geocoding(Fixture.Servers, "nominatim");

        var result = await client.GeocodeAsync("Bondi Beach", CancellationToken.None);

        result.Should().NotBeNull();
        result!.FormattedAddress.Should().Contain("Bondi Beach");
        result.Location.X.Should().BeApproximately(151.2796, 0.0001);
        result.Location.Y.Should().BeApproximately(-33.8908, 0.0001);
    }

    [Fact]
    public async Task Nominatim_ReverseGeocodeAsync_returns_address()
    {
        var client = ClientFactory.Geocoding(Fixture.Servers, "nominatim");

        var point = Geom.CreatePoint(new Coordinate(151.2106, -33.8615));
        var result = await client.ReverseGeocodeAsync(point, CancellationToken.None);

        result.Should().NotBeNull();
        result!.FormattedAddress.Should().Contain("Circular Quay");
    }

    [Fact]
    public async Task Google_GeocodeAsync_picks_first_result_and_maps_precision()
    {
        var client = ClientFactory.Geocoding(Fixture.Servers, "google");

        var result = await client.GeocodeAsync("Circular Quay", CancellationToken.None);

        result.Should().NotBeNull();
        result!.FormattedAddress.Should().Be("Circular Quay, Sydney NSW 2000, Australia");
        result.Precision.Should().Be(Trips.Core.Abstractions.GeocodingPrecision.Rooftop);
    }
}
