using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using Trips.Integrations.Clients;

namespace Trips.Integrations.Tests;

/// <summary>
/// Exercises <see cref="OsrmRoutesClient"/> against a captured <see cref="HttpMessageHandler"/> so
/// the test runs without a live OSRM. Pins the <c>/table</c> request shape (lon,lat order; sources
/// then destinations indices) and the seconds→minutes / null→infinity parsing contract.
/// </summary>
public sealed class OsrmRoutesClientTests
{
    private static readonly GeometryFactory Geom = new(new PrecisionModel(), 4326);

    [Fact]
    public async Task ComputeMatrixAsync_builds_table_request_and_parses_minutes()
    {
        var handler = new CapturingHandler("""{"code":"Ok","durations":[[0,120],[180,0]]}""");
        var client = BuildClient(handler);

        var origins = new[] { P(151.2093, -33.8688), P(151.2509, -33.8919) };

        var matrix = await client.ComputeMatrixAsync(origins, origins, CancellationToken.None);

        // 120s ⇒ 2 min, 180s ⇒ 3 min.
        matrix[0, 1].Should().BeApproximately(2.0, 1e-9);
        matrix[1, 0].Should().BeApproximately(3.0, 1e-9);

        var uri = handler.LastRequest!.RequestUri!.ToString();
        uri.Should().Contain("/table/v1/driving/");
        // Coordinates are lon,lat (X first) and origins precede destinations in the coord list.
        uri.Should().Contain("151.209300,-33.868800");
        uri.Should().Contain("sources=0;1");
        uri.Should().Contain("destinations=2;3");
        uri.Should().Contain("annotations=duration");
    }

    [Fact]
    public async Task ComputeMatrixAsync_maps_null_duration_to_infinity()
    {
        var handler = new CapturingHandler("""{"code":"Ok","durations":[[0,null]]}""");
        var client = BuildClient(handler);

        var origins = new[] { P(151.0, -33.0) };
        var dests = new[] { P(151.0, -33.0), P(152.0, -34.0) };

        var matrix = await client.ComputeMatrixAsync(origins, dests, CancellationToken.None);

        double.IsPositiveInfinity(matrix[0, 1]).Should().BeTrue("OSRM null means no route — the runner keeps its haversine estimate for that pair");
    }

    [Fact]
    public async Task ComputeMatrixAsync_returns_all_infinity_when_osrm_reports_failure()
    {
        var handler = new CapturingHandler("""{"code":"NoTable"}""");
        var client = BuildClient(handler);

        var origins = new[] { P(151.0, -33.0) };
        var dests = new[] { P(152.0, -34.0) };

        var matrix = await client.ComputeMatrixAsync(origins, dests, CancellationToken.None);

        double.IsPositiveInfinity(matrix[0, 0]).Should().BeTrue();
    }

    [Fact]
    public async Task ComputeMatrixAsync_short_circuits_empty_input()
    {
        var handler = new CapturingHandler("""{"code":"Ok","durations":[]}""");
        var client = BuildClient(handler);

        var matrix = await client.ComputeMatrixAsync(Array.Empty<Point>(), new[] { P(151.0, -33.0) }, CancellationToken.None);

        matrix.GetLength(0).Should().Be(0);
        handler.LastRequest.Should().BeNull("no upstream call should be made when there are no origins");
    }

    private static Point P(double lng, double lat) => Geom.CreatePoint(new Coordinate(lng, lat));

    private static OsrmRoutesClient BuildClient(HttpMessageHandler handler)
        => new(new HttpClient(handler) { BaseAddress = new Uri("http://osrm.test") }, NullLogger<OsrmRoutesClient>.Instance);

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly string _json;

        public CapturingHandler(string json) => _json = json;

        public HttpRequestMessage? LastRequest { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastRequest = request;
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_json, Encoding.UTF8, "application/json"),
            });
        }
    }
}
