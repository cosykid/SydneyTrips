using FluentAssertions;
using NetTopologySuite.Geometries;
using Trips.Integrations.Caching;

namespace Trips.Integrations.Tests;

public sealed class CacheKeyTests
{
    private static readonly GeometryFactory Geom = new(new PrecisionModel(), 4326);

    [Fact]
    public void Same_inputs_produce_same_key()
    {
        var p = Geom.CreatePoint(new Coordinate(151.21, -33.87));
        var k1 = CacheKey.Build("ns", p, 800, "extra");
        var k2 = CacheKey.Build("ns", p, 800, "extra");
        k1.Should().Be(k2);
    }

    [Fact]
    public void Different_radius_produces_different_key()
    {
        var p = Geom.CreatePoint(new Coordinate(151.21, -33.87));
        var k1 = CacheKey.Build("ns", p, 800);
        var k2 = CacheKey.Build("ns", p, 1200);
        k1.Should().NotBe(k2);
    }

    [Fact]
    public void Point_components_are_compared_with_six_decimal_precision()
    {
        var p1 = Geom.CreatePoint(new Coordinate(151.2100001, -33.8700001));
        var p2 = Geom.CreatePoint(new Coordinate(151.2100002, -33.8700002));
        var k1 = CacheKey.Build("ns", p1);
        var k2 = CacheKey.Build("ns", p2);
        // 6dp rounds 151.21000010 and 151.21000020 to "151.210000" and "151.210000" — same key.
        k1.Should().Be(k2);
    }

    [Fact]
    public void Namespace_prefixes_the_key()
    {
        var p = Geom.CreatePoint(new Coordinate(151.21, -33.87));
        CacheKey.Build("alpha", p).Should().StartWith("alpha:");
        CacheKey.Build("beta", p).Should().StartWith("beta:");
    }
}
