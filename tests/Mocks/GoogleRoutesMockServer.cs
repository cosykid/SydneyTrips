using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Settings;

namespace Trips.Mocks;

/// <summary>
/// WireMock-backed mock of <c>routes.googleapis.com</c> + Google Geocoding.
/// We mount both surfaces on the same WireMock instance so the test fixture can use one URL,
/// and the integration client just points its two HttpClient base addresses at the same host.
/// </summary>
public sealed class GoogleRoutesMockServer : IDisposable
{
    private readonly WireMockServer _server;

    public string BaseUrl => _server.Url ?? throw new InvalidOperationException("Mock server not running");

    /// <summary>Same URL — the Geocoding paths live under <c>/maps/api/geocode/json</c> on this stub.</summary>
    public string GeocodingBaseUrl => BaseUrl;

    private GoogleRoutesMockServer(WireMockServer server, string fixturesRoot)
    {
        _server = server;
        RegisterStubs(fixturesRoot);
    }

    public static GoogleRoutesMockServer Start(string fixturesRoot) => Start(fixturesRoot, null);

    public static GoogleRoutesMockServer Start(string fixturesRoot, int? port)
    {
        var settings = new WireMockServerSettings
        {
            StartAdminInterface = false,
            ReadStaticMappings = false,
        };
        if (port is int p)
        {
            settings.Port = p;
            settings.Urls = new[] { $"http://127.0.0.1:{p}/" };
        }
        var server = WireMockServer.Start(settings);
        return new GoogleRoutesMockServer(server, fixturesRoot);
    }

    private void RegisterStubs(string fixturesRoot)
    {
        var matrixBody = FixturePaths.ReadAll(fixturesRoot, "google/matrix-cbd-to-bondi.json");
        var routesBody = FixturePaths.ReadAll(fixturesRoot, "google/routes-bondi-loop.json");
        var geocodeBody = FixturePaths.ReadAll(fixturesRoot, "google/geocode-circular-quay.json");

        _server
            .Given(Request.Create()
                .WithPath("/distanceMatrix/v2:computeRouteMatrix")
                .UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(matrixBody));

        _server
            .Given(Request.Create()
                .WithPath("/directions/v2:computeRoutes")
                .UsingPost())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(routesBody));

        _server
            .Given(Request.Create()
                .WithPath("/maps/api/geocode/json")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(geocodeBody));
    }

    public void Dispose()
    {
        _server.Stop();
        _server.Dispose();
    }
}
