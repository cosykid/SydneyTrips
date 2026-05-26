using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Extensions.Logging;
using NetTopologySuite.Geometries;

namespace Trips.Integrations.Clients;

/// <summary>
/// <see cref="IFreeFlowMatrixClient"/> backed by a self-hosted OSRM (<c>osrm-routed</c>) instance.
/// One <c>/table</c> request returns the whole origins×destinations duration matrix in a single
/// local call at zero marginal cost — replacing Google's per-element Route Matrix on the planning
/// path. OSRM has no notion of live traffic, which is exactly right here: the planner solves against
/// a future departure where a traffic snapshot taken now would be noise.
/// </summary>
internal sealed class OsrmRoutesClient : IFreeFlowMatrixClient
{
    /// <summary>Name used by <see cref="IHttpClientFactory"/> to resolve the configured client.</summary>
    public const string HttpClientName = "Osrm";

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ILogger<OsrmRoutesClient> _logger;

    public OsrmRoutesClient(HttpClient http, ILogger<OsrmRoutesClient> logger)
    {
        _http = http;
        _logger = logger;
    }

    public async Task<double[,]> ComputeMatrixAsync(
        IReadOnlyList<Point> origins,
        IReadOnlyList<Point> destinations,
        CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(origins);
        ArgumentNullException.ThrowIfNull(destinations);

        var result = new double[origins.Count, destinations.Count];
        if (origins.Count == 0 || destinations.Count == 0)
        {
            return result;
        }

        // OSRM's /table takes a single coordinate list and indexes into it with `sources` and
        // `destinations`. We concatenate origins then destinations so sources are [0, O) and
        // destinations are [O, O+D). Coordinates are lon,lat — same order as PostGIS Point (X=lng).
        var url = BuildTableUrl(origins, destinations);

        using var scope = _logger.BeginScope(new Dictionary<string, object?>
        {
            ["OsrmEndpoint"] = "table",
            ["OriginCount"] = origins.Count,
            ["DestinationCount"] = destinations.Count,
        });
        _logger.LogInformation("Calling OSRM table");

        using var response = await _http.GetAsync(url, ct).ConfigureAwait(false);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(ct).ConfigureAwait(false);
        var payload = await JsonSerializer.DeserializeAsync<TableResponse>(stream, JsonOptions, ct).ConfigureAwait(false);

        if (payload is null || !string.Equals(payload.Code, "Ok", StringComparison.Ordinal) || payload.Durations is null)
        {
            // OSRM couldn't service the request (e.g. NoTable / out-of-bounds coordinates). Surface
            // every pair as unroutable; HybridRoutesClient lets this propagate so the runner keeps
            // its haversine estimate rather than silently falling back to billable Google calls.
            _logger.LogWarning("OSRM table returned code {Code}; treating all pairs as unroutable", payload?.Code ?? "(none)");
            FillUnroutable(result);
            return result;
        }

        for (var i = 0; i < origins.Count; i++)
        {
            var row = i < payload.Durations.Count ? payload.Durations[i] : null;
            for (var j = 0; j < destinations.Count; j++)
            {
                // OSRM durations are seconds; null means it found no route between the snapped pair.
                var seconds = row is not null && j < row.Count ? row[j] : null;
                result[i, j] = seconds is { } s ? s / 60.0 : double.PositiveInfinity;
            }
        }

        return result;
    }

    private static string BuildTableUrl(IReadOnlyList<Point> origins, IReadOnlyList<Point> destinations)
    {
        var coords = new StringBuilder();
        AppendCoords(coords, origins);
        coords.Append(';');
        AppendCoords(coords, destinations);

        var sources = string.Join(';', Enumerable.Range(0, origins.Count));
        var dests = string.Join(';', Enumerable.Range(origins.Count, destinations.Count));

        // skip_waypoints trims the snapped-coordinate echo we don't use; annotations=duration keeps
        // the response to just the matrix.
        return $"/table/v1/driving/{coords}?sources={sources}&destinations={dests}&annotations=duration&skip_waypoints=true";
    }

    private static void AppendCoords(StringBuilder sb, IReadOnlyList<Point> points)
    {
        for (var i = 0; i < points.Count; i++)
        {
            if (i > 0)
            {
                sb.Append(';');
            }
            sb.Append(points[i].X.ToString("F6", CultureInfo.InvariantCulture));
            sb.Append(',');
            sb.Append(points[i].Y.ToString("F6", CultureInfo.InvariantCulture));
        }
    }

    private static void FillUnroutable(double[,] matrix)
    {
        for (var i = 0; i < matrix.GetLength(0); i++)
        {
            for (var j = 0; j < matrix.GetLength(1); j++)
            {
                matrix[i, j] = double.PositiveInfinity;
            }
        }
    }

    private sealed class TableResponse
    {
        public string? Code { get; set; }

        [JsonPropertyName("durations")]
        public List<List<double?>>? Durations { get; set; }
    }
}
