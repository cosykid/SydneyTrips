using Google.Protobuf;
using WireMock.Matchers;
using WireMock.RequestBuilders;
using WireMock.ResponseBuilders;
using WireMock.Server;
using WireMock.Settings;

namespace Trips.Mocks;

/// <summary>
/// WireMock-backed mock of <c>api.transport.nsw.gov.au</c>. Serves canned trip plans,
/// nearby-stop responses, departure monitors, and a synthetic GTFS-Realtime protobuf
/// feed. Routes by query-string content (origin/destination coordinates and stop ids)
/// so callers exercise something close to the real path-matching the live API does.
/// </summary>
public sealed class TfNswMockServer : IDisposable
{
    private readonly WireMockServer _server;

    public string BaseUrl => _server.Url ?? throw new InvalidOperationException("Mock server not running");

    /// <summary>The full request URLs (path + query) of every trip-plan call received, oldest first.
    /// Lets tests assert the client formats <c>itdDate</c>/<c>itdTime</c> in Sydney local time.</summary>
    public IReadOnlyList<string> TripPlanRequestUrls() =>
        _server.LogEntries
            .Where(e => e.RequestMessage.Path == "/v1/tp/trip")
            .Select(e => e.RequestMessage.Url)
            .ToList();

    private TfNswMockServer(WireMockServer server, string fixturesRoot)
    {
        _server = server;
        RegisterStubs(fixturesRoot);
    }

    /// <summary>Start the mock on a dynamic port.</summary>
    public static TfNswMockServer Start(string fixturesRoot) =>
        Start(fixturesRoot, port: null);

    /// <summary>Start the mock on a fixed port (used by the standalone <c>dotnet run -- start</c> mode).</summary>
    public static TfNswMockServer Start(string fixturesRoot, int? port)
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
        return new TfNswMockServer(server, fixturesRoot);
    }

    private void RegisterStubs(string fixturesRoot)
    {
        // Trip-plan: register one stub per fixture, distinguished by origin-coord substring.
        // Last-match-wins in WireMock, so the most specific stubs come last.
        RegisterTripPlan(fixturesRoot, "151.2073", "tfnsw/trip-cbd-to-bondi.json");
        RegisterTripPlan(fixturesRoot, "151.0021", "tfnsw/trip-parramatta-to-manly.json");
        RegisterTripPlan(fixturesRoot, "151.1832", "tfnsw/trip-chatswood-to-cronulla.json");
        // Two journeys: a faster 343 bus (ranked first by EFA) vs a slightly slower but
        // higher-frequency L3 light rail. Exercises MapTripPlan's cost-based selection.
        RegisterTripPlan(fixturesRoot, "151.1934", "tfnsw/trip-mascot-bus-vs-lightrail.json");

        // Fallback trip-plan when no other match — registered first so others win.
        _server
            .Given(Request.Create().WithPath("/v1/tp/trip").UsingGet())
            .AtPriority(100)
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(FixturePaths.ReadAll(fixturesRoot, "tfnsw/trip-cbd-to-bondi.json")));

        // Coordinate request: route by the coord param.
        RegisterCoord(fixturesRoot, "151.207", "tfnsw/coord-cbd.json");
        RegisterCoord(fixturesRoot, "151.002", "tfnsw/coord-parramatta.json");
        RegisterCoord(fixturesRoot, "151.183", "tfnsw/coord-chatswood.json");
        // Coord fallback.
        _server
            .Given(Request.Create().WithPath("/v1/tp/coord").UsingGet())
            .AtPriority(100)
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(FixturePaths.ReadAll(fixturesRoot, "tfnsw/coord-cbd.json")));

        // Departure monitor — single fixture covers any stop.
        _server
            .Given(Request.Create().WithPath("/v1/tp/departure_mon").UsingGet())
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(FixturePaths.ReadAll(fixturesRoot, "tfnsw/departure-town-hall.json")));

        // GTFS-Realtime feeds: synthetic protobuf served on every supported mode endpoint.
        var protoBytes = SyntheticGtfsFeed.Build();
        foreach (var path in new[]
                 {
                     "/v1/gtfs/realtime/sydneytrains",
                     "/v1/gtfs/realtime/buses",
                     "/v1/gtfs/realtime/ferries",
                     "/v1/gtfs/realtime/lightrail",
                 })
        {
            _server
                .Given(Request.Create().WithPath(path).UsingGet())
                .RespondWith(Response.Create()
                    .WithStatusCode(200)
                    .WithHeader("Content-Type", "application/x-google-protobuf")
                    .WithBody(protoBytes));
        }
    }

    private void RegisterTripPlan(string fixturesRoot, string originLngFragment, string fixtureFile)
    {
        _server
            .Given(Request.Create()
                .WithPath("/v1/tp/trip")
                .WithParam("name_origin", MatchBehaviour.AcceptOnMatch, new WildcardMatcher($"*{originLngFragment}*"))
                .UsingGet())
            .AtPriority(10)
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(FixturePaths.ReadAll(fixturesRoot, fixtureFile)));
    }

    private void RegisterCoord(string fixturesRoot, string lngFragment, string fixtureFile)
    {
        _server
            .Given(Request.Create()
                .WithPath("/v1/tp/coord")
                .WithParam("coord", MatchBehaviour.AcceptOnMatch, new WildcardMatcher($"{lngFragment}*"))
                .UsingGet())
            .AtPriority(10)
            .RespondWith(Response.Create()
                .WithStatusCode(200)
                .WithHeader("Content-Type", "application/json")
                .WithBody(FixturePaths.ReadAll(fixturesRoot, fixtureFile)));
    }

    public void Dispose()
    {
        _server.Stop();
        _server.Dispose();
    }
}

/// <summary>
/// Builds a minimal GTFS-Realtime feed in memory so tests do not need to commit binary fixtures.
/// </summary>
internal static class SyntheticGtfsFeed
{
    public static byte[] Build()
    {
        var nowSeconds = (ulong)DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        var feed = new Trips.Integrations.Protos.GtfsRealtime.FeedMessage
        {
            Header = new Trips.Integrations.Protos.GtfsRealtime.FeedHeader
            {
                GtfsRealtimeVersion = "2.0",
                Incrementality = Trips.Integrations.Protos.GtfsRealtime.FeedHeader.Types.Incrementality.FullDataset,
                Timestamp = nowSeconds,
            },
        };
        feed.Entity.Add(new Trips.Integrations.Protos.GtfsRealtime.FeedEntity
        {
            Id = "trip-873",
            TripUpdate = new Trips.Integrations.Protos.GtfsRealtime.TripUpdate
            {
                Trip = new Trips.Integrations.Protos.GtfsRealtime.TripDescriptor { TripId = "T4-873-2025-01-15", RouteId = "T4" },
                Vehicle = new Trips.Integrations.Protos.GtfsRealtime.VehicleDescriptor { Id = "TRAIN-873" },
                Timestamp = nowSeconds,
                StopTimeUpdate =
                {
                    new Trips.Integrations.Protos.GtfsRealtime.TripUpdate.Types.StopTimeUpdate
                    {
                        StopId = "200060",
                        Departure = new Trips.Integrations.Protos.GtfsRealtime.TripUpdate.Types.StopTimeEvent { Time = (long)nowSeconds + 60 },
                    },
                    new Trips.Integrations.Protos.GtfsRealtime.TripUpdate.Types.StopTimeUpdate
                    {
                        StopId = "200030",
                        Arrival = new Trips.Integrations.Protos.GtfsRealtime.TripUpdate.Types.StopTimeEvent { Time = (long)nowSeconds + 540 },
                    },
                },
            },
        });
        feed.Entity.Add(new Trips.Integrations.Protos.GtfsRealtime.FeedEntity
        {
            Id = "trip-128",
            TripUpdate = new Trips.Integrations.Protos.GtfsRealtime.TripUpdate
            {
                Trip = new Trips.Integrations.Protos.GtfsRealtime.TripDescriptor { TripId = "T2-128-2025-01-15", RouteId = "T2" },
                Timestamp = nowSeconds,
                StopTimeUpdate =
                {
                    new Trips.Integrations.Protos.GtfsRealtime.TripUpdate.Types.StopTimeUpdate
                    {
                        StopId = "200060",
                        Departure = new Trips.Integrations.Protos.GtfsRealtime.TripUpdate.Types.StopTimeEvent { Time = (long)nowSeconds + 300 },
                    },
                },
            },
        });

        return feed.ToByteArray();
    }
}
