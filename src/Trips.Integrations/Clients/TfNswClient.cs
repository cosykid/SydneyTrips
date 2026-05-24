using System.Globalization;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Google.Protobuf;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NetTopologySuite.Geometries;
using Trips.Core.Abstractions;
using Trips.Integrations.Configuration;
using Trips.Integrations.Protos.GtfsRealtime;

namespace Trips.Integrations.Clients;

/// <summary>
/// HttpClient-based implementation of <see cref="ITfNswClient"/>. Talks to
/// <c>api.transport.nsw.gov.au</c> using <c>rapidJSON</c> output where supported
/// and the GTFS-Realtime protobuf feed for vehicle updates.
/// </summary>
/// <remarks>
/// Resilience is wired at the <see cref="HttpClient"/> level via <c>AddStandardResilienceHandler</c>,
/// and caching is layered by <see cref="Caching.CachingTfNswClient"/>. Configuration is
/// resolved from <see cref="TfNswOptions"/>.
/// </remarks>
internal sealed class TfNswClient : ITfNswClient
{
    /// <summary>Name used by <see cref="IHttpClientFactory"/> to resolve the configured client.</summary>
    public const string HttpClientName = "TfNsw";

    private static readonly GeometryFactory GeometryFactory =
        new(new PrecisionModel(), 4326);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly TfNswOptions _options;
    private readonly ILogger<TfNswClient> _logger;

    public TfNswClient(HttpClient http, IOptions<TfNswOptions> options, ILogger<TfNswClient> logger)
    {
        _http = http;
        _options = options.Value;
        _logger = logger;
    }

    public async Task<TfNswTripPlan> TripPlanAsync(
        Point origin,
        Point destination,
        DateTimeOffset departAt,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(destination);

        var qs = new QueryStringBuilder()
            .Add("outputFormat", "rapidJSON")
            .Add("coordOutputFormat", "EPSG:4326")
            .Add("depArrMacro", "dep")
            .Add("itdDate", departAt.ToString("yyyyMMdd", CultureInfo.InvariantCulture))
            .Add("itdTime", departAt.ToString("HHmm", CultureInfo.InvariantCulture))
            .Add("type_origin", "coord")
            .Add("name_origin", $"{origin.X.ToString(CultureInfo.InvariantCulture)}:{origin.Y.ToString(CultureInfo.InvariantCulture)}:EPSG:4326")
            .Add("type_destination", "coord")
            .Add("name_destination", $"{destination.X.ToString(CultureInfo.InvariantCulture)}:{destination.Y.ToString(CultureInfo.InvariantCulture)}:EPSG:4326")
            .Add("calcNumberOfTrips", "1")
            .Build();

        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["TfNswEndpoint"] = "trip",
            ["Origin"] = $"{origin.Y:F4},{origin.X:F4}",
            ["Destination"] = $"{destination.Y:F4},{destination.X:F4}",
        });

        _logger.LogInformation("Calling TfNSW Trip Planner");
        using var response = await _http.GetAsync($"/v1/tp/trip?{qs}", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var payload = await JsonSerializer.DeserializeAsync<TripPlanResponse>(stream, JsonOptions, ct).ConfigureAwait(false);

        return MapTripPlan(payload);
    }

    public async Task<IReadOnlyList<TfNswCoordinateStop>> CoordinateRequestAsync(
        Point origin,
        int radiusMeters,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(origin);
        if (radiusMeters <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(radiusMeters), radiusMeters, "radius must be positive");
        }

        // EFA's coord endpoint returns nothing when you use the `_sf` (single-field free-text)
        // params for a radius lookup. The working pattern is the indexed-filter form:
        // `inclFilter=1` activates the filter list, `type_1=STOP` says "give me transit stops",
        // `radius_1` bounds the search. Probed live 2026-05; without these, the API answers 200
        // with `"locations":[]` for even Sydney CBD.
        var coord = $"{origin.X.ToString(CultureInfo.InvariantCulture)}:{origin.Y.ToString(CultureInfo.InvariantCulture)}:EPSG:4326";
        var qs = new QueryStringBuilder()
            .Add("outputFormat", "rapidJSON")
            .Add("coord", coord)
            .Add("coordOutputFormat", "EPSG:4326")
            .Add("inclFilter", "1")
            .Add("type_1", "STOP")
            .Add("radius_1", radiusMeters.ToString(CultureInfo.InvariantCulture))
            .Build();

        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["TfNswEndpoint"] = "coord",
            ["Origin"] = $"{origin.Y:F4},{origin.X:F4}",
            ["RadiusMeters"] = radiusMeters,
        });

        _logger.LogInformation("Calling TfNSW Coordinate Request");
        using var response = await _http.GetAsync($"/v1/tp/coord?{qs}", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var payload = await JsonSerializer.DeserializeAsync<CoordResponse>(stream, JsonOptions, ct).ConfigureAwait(false);

        return MapCoordResponse(payload);
    }

    public async Task<IReadOnlyList<TfNswDeparture>> DepartureAsync(
        string stopId,
        DateTimeOffset @from,
        CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(stopId);

        var qs = new QueryStringBuilder()
            .Add("outputFormat", "rapidJSON")
            .Add("type_dm", "stop")
            .Add("name_dm", stopId)
            .Add("itdDate", from.ToString("yyyyMMdd", CultureInfo.InvariantCulture))
            .Add("itdTime", from.ToString("HHmm", CultureInfo.InvariantCulture))
            .Add("mode", "direct")
            .Add("useRealtime", "true")
            .Build();

        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["TfNswEndpoint"] = "departure_mon",
            ["StopId"] = stopId,
        });

        _logger.LogInformation("Calling TfNSW Departure Monitor");
        using var response = await _http.GetAsync($"/v1/tp/departure_mon?{qs}", ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var payload = await JsonSerializer.DeserializeAsync<DepartureResponse>(stream, JsonOptions, ct).ConfigureAwait(false);

        return MapDepartures(payload, stopId);
    }

    public async IAsyncEnumerable<TfNswGtfsTripUpdate> GtfsRtTripUpdatesAsync(
        string mode,
        [EnumeratorCancellation] CancellationToken ct)
    {
        ArgumentException.ThrowIfNullOrEmpty(mode);

        if (!_options.GtfsRealtimeFeeds.TryGetValue(mode, out var feedPath))
        {
            throw new ArgumentException($"No GTFS-RT feed configured for mode '{mode}'.", nameof(mode));
        }

        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["TfNswEndpoint"] = "gtfs-rt",
            ["Mode"] = mode,
            ["FeedPath"] = feedPath,
        });

        _logger.LogInformation("Calling TfNSW GTFS-Realtime feed");
        using var request = new HttpRequestMessage(HttpMethod.Get, feedPath);
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/x-google-protobuf"));
        using var response = await _http.SendAsync(request, HttpCompletionOption.ResponseContentRead, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var feed = FeedMessage.Parser.ParseFrom(stream);

        foreach (var entity in feed.Entity)
        {
            ct.ThrowIfCancellationRequested();
            if (entity.TripUpdate is null)
            {
                continue;
            }

            yield return MapTripUpdate(entity.TripUpdate, feed.Header);
        }
    }

    // ----- Mapping helpers -----

    private static TfNswTripPlan MapTripPlan(TripPlanResponse? payload)
    {
        if (payload?.Journeys is null || payload.Journeys.Count == 0)
        {
            return new TfNswTripPlan(Array.Empty<TfNswJourneyLeg>(), 0, 0);
        }

        var first = payload.Journeys[0];
        var legs = new List<TfNswJourneyLeg>(first.Legs?.Count ?? 0);
        int walk = 0;
        int pt = 0;

        if (first.Legs is not null)
        {
            foreach (var leg in first.Legs)
            {
                var mode = leg.Transportation?.Product?.Class is int c
                    ? ClassifyMode(c)
                    : leg.Transportation?.Product?.Name ?? "unknown";
                var duration = leg.Duration ?? 0;
                var minutes = duration / 60;

                if (mode == "walk")
                {
                    walk += minutes;
                }
                else
                {
                    pt += minutes;
                }

                var from = LegPoint(leg.Origin);
                var to = LegPoint(leg.Destination);
                var polyline = LegPolyline(leg.Coords);
                legs.Add(new TfNswJourneyLeg(
                    mode,
                    minutes,
                    from,
                    to,
                    leg.Transportation?.Number,
                    FromName: leg.Origin?.Name,
                    ToName: leg.Destination?.Name,
                    Polyline: polyline));
            }
        }

        return new TfNswTripPlan(legs, walk, pt);
    }

    private static IReadOnlyList<TfNswCoordinateStop> MapCoordResponse(CoordResponse? payload)
    {
        if (payload?.Locations is null || payload.Locations.Count == 0)
        {
            return Array.Empty<TfNswCoordinateStop>();
        }

        // Real rapidJSON shape for type_1=STOP results (probed live 2026-05): the useful fields
        // live under `properties`, not at the top level:
        //   - properties.distance          → metres from query coord (top-level is null)
        //   - properties.stopId            → numeric EFA stopId, the form TripPlan/Departures want
        //   - properties.STOP_NAME_WITH_PLACE → human name; top-level `name` is literally
        //                                       "undefined, undefined" for stop-type rows.
        // Top-level `id` is a "global id" (e.g. G200077) in a different namespace and is NOT
        // accepted by TripPlanAsync / DepartureAsync.
        //
        // Coord order on this endpoint is [lat, lng]; NTS Point needs (X=lng, Y=lat).
        var ordered = payload.Locations
            .OrderBy(l => l.Properties?.Distance ?? l.Distance ?? int.MaxValue)
            .ToList();
        var results = new List<TfNswCoordinateStop>(ordered.Count);
        foreach (var loc in ordered)
        {
            var coords = loc.Coord;
            if (coords is null || coords.Count < 2)
            {
                continue;
            }

            var point = GeometryFactory.CreatePoint(new Coordinate(coords[1], coords[0]));
            var mode = loc.ProductClasses is { Count: > 0 } pc ? ClassifyMode(pc[0]) : loc.Type ?? "unknown";
            var stopId = loc.Properties?.StopId ?? loc.Id ?? string.Empty;
            if (string.IsNullOrEmpty(stopId))
            {
                continue;
            }
            var name = !string.IsNullOrEmpty(loc.Properties?.StopNameWithPlace)
                ? loc.Properties.StopNameWithPlace
                : !string.IsNullOrEmpty(loc.Name) && loc.Name != "undefined, undefined"
                    ? loc.Name
                    : stopId;
            results.Add(new TfNswCoordinateStop(
                StopId: stopId,
                Name: name,
                Location: point,
                DistanceMeters: loc.Properties?.Distance ?? loc.Distance ?? 0,
                Mode: mode));
        }
        return results;
    }

    private static IReadOnlyList<TfNswDeparture> MapDepartures(DepartureResponse? payload, string stopId)
    {
        if (payload?.StopEvents is null || payload.StopEvents.Count == 0)
        {
            return Array.Empty<TfNswDeparture>();
        }

        var results = new List<TfNswDeparture>(payload.StopEvents.Count);
        foreach (var ev in payload.StopEvents)
        {
            var planned = ev.DepartureTimePlanned ?? ev.DepartureTimeEstimated ?? DateTimeOffset.MinValue;
            var estimated = ev.DepartureTimeEstimated ?? planned;
            var line = ev.Transportation?.Number ?? ev.Transportation?.Name ?? string.Empty;
            var vehicleId = ev.Transportation?.Properties?.RealtimeTripId;
            results.Add(new TfNswDeparture(stopId, line, planned, estimated, vehicleId));
        }
        return results;
    }

    private static TfNswGtfsTripUpdate MapTripUpdate(TripUpdate tu, FeedHeader header)
    {
        var ts = tu.HasTimestamp
            ? DateTimeOffset.FromUnixTimeSeconds((long)tu.Timestamp)
            : header.HasTimestamp
                ? DateTimeOffset.FromUnixTimeSeconds((long)header.Timestamp)
                : DateTimeOffset.UtcNow;

        var stopUpdates = new List<TfNswStopTimeUpdate>(tu.StopTimeUpdate.Count);
        foreach (var su in tu.StopTimeUpdate)
        {
            DateTimeOffset? arrival = su.Arrival?.HasTime == true
                ? DateTimeOffset.FromUnixTimeSeconds(su.Arrival.Time)
                : null;
            DateTimeOffset? departure = su.Departure?.HasTime == true
                ? DateTimeOffset.FromUnixTimeSeconds(su.Departure.Time)
                : null;
            stopUpdates.Add(new TfNswStopTimeUpdate(su.StopId ?? string.Empty, arrival, departure));
        }

        return new TfNswGtfsTripUpdate(
            TripId: tu.Trip?.TripId ?? string.Empty,
            VehicleId: tu.Vehicle?.Id,
            Timestamp: ts,
            StopTimeUpdates: stopUpdates);
    }

    private static Point LegPoint(LegEndpoint? endpoint)
    {
        // EFA rapidJSON returns leg coords as [lat, lng] (same as the /coord endpoint, verified
        // live 2026-05). NTS Point wants (X=lng, Y=lat). Reversing this silently put every
        // PT-leg endpoint somewhere in the Indian Ocean — fed through the haversine travel
        // matrix, that produced ~150-hour drive times and CP-SAT proved the model infeasible
        // before search started.
        if (endpoint?.Coord is { Count: >= 2 } c)
        {
            return GeometryFactory.CreatePoint(new Coordinate(c[1], c[0]));
        }
        return GeometryFactory.CreatePoint(new Coordinate(0, 0));
    }

    /// <summary>
    /// Convert EFA's <c>[[lat,lng], ...]</c> coords array to NTS Points in (X=lng, Y=lat) order.
    /// Returns null when the input is missing/empty so downstream consumers can distinguish
    /// "no geometry available" (legacy stub, cached pre-feature plan) from "empty journey".
    /// </summary>
    private static IReadOnlyList<Point>? LegPolyline(List<List<double>>? coords)
    {
        if (coords is null || coords.Count == 0) return null;
        var points = new List<Point>(coords.Count);
        foreach (var pair in coords)
        {
            if (pair is null || pair.Count < 2) continue;
            points.Add(GeometryFactory.CreatePoint(new Coordinate(pair[1], pair[0])));
        }
        return points.Count > 0 ? points : null;
    }

    private static string ClassifyMode(int productClass) => productClass switch
    {
        1 => "train",
        2 => "metro",
        4 => "lightrail",
        5 => "bus",
        9 => "ferry",
        100 => "walk",
        _ => "unknown",
    };

    // ----- Wire-format DTOs (private, reflect TfNSW rapidJSON shape) -----

    private sealed class TripPlanResponse
    {
        public List<JourneyDto>? Journeys { get; set; }
    }

    private sealed class JourneyDto
    {
        public List<JourneyLegDto>? Legs { get; set; }
    }

    private sealed class JourneyLegDto
    {
        public int? Duration { get; set; }
        public LegEndpoint? Origin { get; set; }
        public LegEndpoint? Destination { get; set; }
        public TransportationDto? Transportation { get; set; }

        /// <summary>
        /// EFA returns the leg's geometry as an array of <c>[lat, lng]</c> doubles — typically
        /// 5–400 points per leg (walks short, transit legs longer). Used to render the real
        /// PT route on the map instead of a crow-fly straight line.
        /// </summary>
        public List<List<double>>? Coords { get; set; }
    }

    private sealed class LegEndpoint
    {
        public string? Name { get; set; }
        public List<double>? Coord { get; set; }
    }

    private sealed class TransportationDto
    {
        public string? Name { get; set; }
        public string? Number { get; set; }
        public TransportationProductDto? Product { get; set; }
        public TransportationPropertiesDto? Properties { get; set; }
    }

    private sealed class TransportationProductDto
    {
        public string? Name { get; set; }
        public int? Class { get; set; }
    }

    private sealed class TransportationPropertiesDto
    {
        public string? RealtimeTripId { get; set; }
    }

    private sealed class CoordResponse
    {
        public List<CoordLocationDto>? Locations { get; set; }
    }

    private sealed class CoordLocationDto
    {
        public string? Id { get; set; }
        public string? Name { get; set; }
        public string? Type { get; set; }
        public List<double>? Coord { get; set; }
        // Top-level `distance` is usually null on the coord endpoint — the real value lives under
        // `properties`. Kept here purely as a graceful fallback if a future API revision repopulates it.
        public int? Distance { get; set; }
        public List<int>? ProductClasses { get; set; }
        public CoordPropertiesDto? Properties { get; set; }
    }

    private sealed class CoordPropertiesDto
    {
        public int? Distance { get; set; }

        /// <summary>EFA numeric stopId — the form TripPlan/Departures endpoints accept.</summary>
        [System.Text.Json.Serialization.JsonPropertyName("stopId")]
        public string? StopId { get; set; }

        /// <summary>Human-readable stop name with locality. Top-level <c>name</c> is "undefined, undefined" for stop rows.</summary>
        [System.Text.Json.Serialization.JsonPropertyName("STOP_NAME_WITH_PLACE")]
        public string? StopNameWithPlace { get; set; }
    }

    private sealed class DepartureResponse
    {
        public List<StopEventDto>? StopEvents { get; set; }
    }

    private sealed class StopEventDto
    {
        public DateTimeOffset? DepartureTimePlanned { get; set; }
        public DateTimeOffset? DepartureTimeEstimated { get; set; }
        public TransportationDto? Transportation { get; set; }
    }
}

/// <summary>
/// Tiny helper that escapes query-string parameters consistently and emits them in insertion order.
/// </summary>
internal sealed class QueryStringBuilder
{
    private readonly List<(string Key, string Value)> _pairs = new();

    public QueryStringBuilder Add(string key, string value)
    {
        _pairs.Add((key, value));
        return this;
    }

    public string Build() =>
        string.Join('&', _pairs.Select(p => $"{Uri.EscapeDataString(p.Key)}={Uri.EscapeDataString(p.Value)}"));
}
