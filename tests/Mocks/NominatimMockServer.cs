using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Settings;

namespace Trips.Mocks;

/// <summary>WireMock-backed mock of <c>nominatim.openstreetmap.org</c>.</summary>
public sealed class NominatimMockServer : IDisposable
{
    private readonly WireMockServer _server;
    public string BaseUrl => _server.Url ?? throw new InvalidOperationException("Mock server not running");

    private NominatimMockServer(WireMockServer server, string fixturesRoot)
    {
        _server = server;
        RegisterStubs(fixturesRoot);
    }

    public static NominatimMockServer Start(string fixturesRoot) => Start(fixturesRoot, null);

    public static NominatimMockServer Start(string fixturesRoot, int? port)
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
        return new NominatimMockServer(server, fixturesRoot);
    }

    private void RegisterStubs(string fixturesRoot)
    {
        var searchBody = FixturePaths.ReadAll(fixturesRoot, "nominatim/search-bondi-beach.json");
        var reverseBody = FixturePaths.ReadAll(fixturesRoot, "nominatim/reverse-circular-quay.json");

        _server
            .Given(Request.Create()
                .WithPath("/search")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(searchBody));

        _server
            .Given(Request.Create()
                .WithPath("/reverse")
                .UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(reverseBody));
    }

    public void Dispose()
    {
        _server.Stop();
        _server.Dispose();
    }
}
