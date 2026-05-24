using System.Net;
using System.Text;
using System.Text.Json;
using FluentAssertions;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using Trips.Integrations.Clients;

namespace Trips.Integrations.Tests;

/// <summary>
/// Pins the wire-shape of the Google Routes requests by capturing the serialised body with a stub
/// handler. The mock-server-backed <see cref="GoogleRoutesClientTests"/> can't catch shape bugs —
/// the mock returns a canned response regardless of what we send — which is exactly how the
/// computeRouteMatrix 400 (origins sent as bare waypoints instead of the required
/// <c>{ "waypoint": { … } }</c> envelope) went unnoticed until the runner started calling it.
/// </summary>
public sealed class GoogleRoutesRequestShapeTests
{
    private static readonly GeometryFactory Geom = new(new PrecisionModel(), 4326);

    [Fact]
    public async Task ComputeRouteMatrix_wraps_each_endpoint_in_a_waypoint_envelope()
    {
        var handler = new CapturingHandler(
            "[{\"originIndex\":0,\"destinationIndex\":0,\"duration\":\"60s\"}]");
        var client = new GoogleRoutesClient(
            new HttpClient(handler) { BaseAddress = new Uri("https://routes.googleapis.test") },
            NullLogger<GoogleRoutesClient>.Instance);

        await client.ComputeRouteMatrixAsync(
            new[] { Geom.CreatePoint(new Coordinate(151.0, -33.8)) },
            new[] { Geom.CreatePoint(new Coordinate(151.1, -33.9)) },
            CancellationToken.None);

        using var doc = JsonDocument.Parse(handler.LastBody!);
        var root = doc.RootElement;

        var origin = root.GetProperty("origins")[0];
        // The matrix API requires the endpoint nested under `waypoint`; sending it bare yields 400.
        origin.TryGetProperty("waypoint", out var wp).Should().BeTrue(
            "computeRouteMatrix origins must be wrapped in a waypoint envelope");
        wp.GetProperty("location").GetProperty("latLng").GetProperty("latitude")
            .GetDouble().Should().BeApproximately(-33.8, 1e-9);
        origin.TryGetProperty("location", out _).Should().BeFalse(
            "the bare-waypoint form (location at the top level) is the bug we're guarding against");

        root.GetProperty("destinations")[0].TryGetProperty("waypoint", out _).Should().BeTrue();
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly string _responseJson;
        public string? LastBody { get; private set; }

        public CapturingHandler(string responseJson) => _responseJson = responseJson;

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            LastBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(_responseJson, Encoding.UTF8, "application/json"),
            };
        }
    }
}
