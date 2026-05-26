using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;
using Trips.Core.Abstractions;

namespace Trips.Integrations.Clients;

/// <summary>
/// HttpClient-based implementation of <see cref="IGoogleRoutesClient"/>. Uses the
/// Routes API v2 (compute_routes + compute_route_matrix) with aggressive field masks
/// to keep payload size — and billing — predictable.
/// </summary>
internal sealed class GoogleRoutesClient : IGoogleRoutesClient
{
    /// <summary>Name used by <see cref="IHttpClientFactory"/> to resolve the configured client.</summary>
    public const string HttpClientName = "GoogleRoutes";

    private const string MatrixFieldMask = "originIndex,destinationIndex,duration,distanceMeters,status";

    private const string RoutesFieldMask =
        "routes.duration,routes.distanceMeters,routes.legs,routes.optimizedIntermediateWaypointIndex,routes.polyline.encodedPolyline";

    private static readonly GeometryFactory GeometryFactory = new(new PrecisionModel(), 4326);

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };

    private readonly HttpClient _http;
    private readonly ILogger<GoogleRoutesClient> _logger;

    public GoogleRoutesClient(HttpClient http, ILogger<GoogleRoutesClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<double[,]> ComputeRouteMatrixAsync(
        IReadOnlyList<Point> origins,
        IReadOnlyList<Point> destinations,
        bool trafficAware,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(origins);
        ArgumentNullException.ThrowIfNull(destinations);

        if (origins.Count == 0 || destinations.Count == 0)
        {
            return new double[origins.Count, destinations.Count];
        }

        var request = new MatrixRequest
        {
            // computeRouteMatrix wraps each endpoint in a RouteMatrixOrigin/Destination whose
            // payload is under a `waypoint` field — unlike computeRoutes, which takes a bare
            // waypoint for `origin`/`destination`. Sending the bare form here yields a 400.
            Origins = origins.Select(BuildMatrixWaypoint).ToList(),
            Destinations = destinations.Select(BuildMatrixWaypoint).ToList(),
            TravelMode = "DRIVE",
            // TRAFFIC_AWARE moves the call onto the pricier "Pro" SKU; TRAFFIC_UNAWARE keeps it on
            // the cheaper "Essentials" SKU. Only the live ETA path asks for traffic — see the
            // trafficAware doc on IGoogleRoutesClient.
            RoutingPreference = trafficAware ? "TRAFFIC_AWARE" : "TRAFFIC_UNAWARE",
        };

        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["GoogleEndpoint"] = "computeRouteMatrix",
            ["OriginCount"] = origins.Count,
            ["DestinationCount"] = destinations.Count,
        });

        _logger.LogInformation("Calling Google computeRouteMatrix");
        using var message = new HttpRequestMessage(HttpMethod.Post, "/distanceMatrix/v2:computeRouteMatrix")
        {
            Content = JsonContent.Create(request, options: JsonOptions),
        };
        message.Headers.Add("X-Goog-FieldMask", MatrixFieldMask);

        using var response = await _http.SendAsync(message, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        var matrix = new double[origins.Count, destinations.Count];
        for (int i = 0; i < origins.Count; i++)
        {
            for (int j = 0; j < destinations.Count; j++)
            {
                matrix[i, j] = double.PositiveInfinity;
            }
        }

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var entries = await JsonSerializer.DeserializeAsync<List<MatrixEntry>>(stream, JsonOptions, ct).ConfigureAwait(false);
        if (entries is null)
        {
            return matrix;
        }

        foreach (var entry in entries)
        {
            if (entry.OriginIndex is int oi
                && entry.DestinationIndex is int di
                && oi < origins.Count
                && di < destinations.Count)
            {
                var minutes = ParseDurationToMinutes(entry.Duration);
                matrix[oi, di] = minutes;
            }
        }

        return matrix;
    }

    public async Task<GoogleRoutesResult> ComputeRoutesAsync(
        Point origin,
        Point destination,
        IReadOnlyList<Point> waypoints,
        bool optimizeWaypointOrder,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(destination);
        ArgumentNullException.ThrowIfNull(waypoints);

        var request = new RoutesRequest
        {
            Origin = BuildWaypoint(origin),
            Destination = BuildWaypoint(destination),
            Intermediates = waypoints.Select(BuildWaypoint).ToList(),
            OptimizeWaypointOrder = optimizeWaypointOrder,
            TravelMode = "DRIVE",
            RoutingPreference = "TRAFFIC_AWARE",
            ComputeAlternativeRoutes = false,
        };

        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["GoogleEndpoint"] = "computeRoutes",
            ["WaypointCount"] = waypoints.Count,
            ["OptimizeOrder"] = optimizeWaypointOrder,
        });

        _logger.LogInformation("Calling Google computeRoutes");
        using var message = new HttpRequestMessage(HttpMethod.Post, "/directions/v2:computeRoutes")
        {
            Content = JsonContent.Create(request, options: JsonOptions),
        };
        message.Headers.Add("X-Goog-FieldMask", RoutesFieldMask);

        using var response = await _http.SendAsync(message, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var payload = await JsonSerializer.DeserializeAsync<RoutesResponse>(stream, JsonOptions, ct).ConfigureAwait(false);

        var route = payload?.Routes?.FirstOrDefault();
        if (route is null)
        {
            return new GoogleRoutesResult(
                Legs: Array.Empty<GoogleRouteLeg>(),
                TotalDurationMins: 0,
                TotalDistanceMeters: 0,
                OptimisedWaypointOrder: null);
        }

        var legs = (route.Legs ?? new List<RouteLeg>())
            .Select(leg => new GoogleRouteLeg(
                From: PointFromLatLng(leg.StartLocation),
                To: PointFromLatLng(leg.EndLocation),
                DurationMins: ParseDurationToMinutes(leg.Duration),
                DistanceMeters: leg.DistanceMeters ?? 0))
            .ToList();

        return new GoogleRoutesResult(
            Legs: legs,
            TotalDurationMins: ParseDurationToMinutes(route.Duration),
            TotalDistanceMeters: route.DistanceMeters ?? 0,
            OptimisedWaypointOrder: route.OptimizedIntermediateWaypointIndex);
    }

    private static Waypoint BuildWaypoint(Point p) => new()
    {
        Location = new WaypointLocation
        {
            LatLng = new LatLng { Latitude = p.Y, Longitude = p.X },
        },
    };

    private static RouteMatrixWaypoint BuildMatrixWaypoint(Point p) => new() { Waypoint = BuildWaypoint(p) };

    private static Point PointFromLatLng(LatLngLocation? loc)
    {
        if (loc?.LatLng is null)
        {
            return GeometryFactory.CreatePoint(new Coordinate(0, 0));
        }
        return GeometryFactory.CreatePoint(new Coordinate(loc.LatLng.Longitude, loc.LatLng.Latitude));
    }

    /// <summary>
    /// Google returns duration as a protobuf-style string like "123s". Convert to minutes.
    /// </summary>
    internal static double ParseDurationToMinutes(string? duration)
    {
        if (string.IsNullOrEmpty(duration))
        {
            return 0;
        }

        if (duration.EndsWith('s')
            && double.TryParse(duration.AsSpan(0, duration.Length - 1), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var seconds))
        {
            return seconds / 60.0;
        }

        if (double.TryParse(duration, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var raw))
        {
            return raw / 60.0;
        }

        return 0;
    }

    // ----- Wire-format DTOs -----

    private sealed class MatrixRequest
    {
        public List<RouteMatrixWaypoint>? Origins { get; set; }
        public List<RouteMatrixWaypoint>? Destinations { get; set; }
        public string? TravelMode { get; set; }
        public string? RoutingPreference { get; set; }
    }

    /// <summary>computeRouteMatrix origin/destination envelope — the waypoint nests under a field.</summary>
    private sealed class RouteMatrixWaypoint
    {
        public Waypoint? Waypoint { get; set; }
    }

    private sealed class RoutesRequest
    {
        public Waypoint? Origin { get; set; }
        public Waypoint? Destination { get; set; }
        public List<Waypoint>? Intermediates { get; set; }
        public bool OptimizeWaypointOrder { get; set; }
        public string? TravelMode { get; set; }
        public string? RoutingPreference { get; set; }
        public bool ComputeAlternativeRoutes { get; set; }
    }

    private sealed class Waypoint
    {
        public WaypointLocation? Location { get; set; }
    }

    private sealed class WaypointLocation
    {
        public LatLng? LatLng { get; set; }
    }

    private sealed class LatLng
    {
        public double Latitude { get; set; }
        public double Longitude { get; set; }
    }

    private sealed class MatrixEntry
    {
        public int? OriginIndex { get; set; }
        public int? DestinationIndex { get; set; }
        public string? Duration { get; set; }
        public double? DistanceMeters { get; set; }
        public StatusPayload? Status { get; set; }
    }

    private sealed class StatusPayload
    {
        public int? Code { get; set; }
        public string? Message { get; set; }
    }

    private sealed class RoutesResponse
    {
        public List<Route>? Routes { get; set; }
    }

    private sealed class Route
    {
        public string? Duration { get; set; }
        public double? DistanceMeters { get; set; }
        public List<RouteLeg>? Legs { get; set; }
        public List<int>? OptimizedIntermediateWaypointIndex { get; set; }
    }

    private sealed class RouteLeg
    {
        public string? Duration { get; set; }
        public double? DistanceMeters { get; set; }
        public LatLngLocation? StartLocation { get; set; }
        public LatLngLocation? EndLocation { get; set; }
    }

    private sealed class LatLngLocation
    {
        public LatLng? LatLng { get; set; }
    }
}
