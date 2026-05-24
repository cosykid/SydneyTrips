using NetTopologySuite.Geometries;
using Trips.Core.Abstractions;

namespace Trips.Api.Stubs;

/// <summary>
/// Stand-in <see cref="ITfNswClient"/> when WS2 isn't merged in.
/// Returns a small synthetic set of stops around a given coordinate so candidate-node generation
/// works in dev and tests. Real impl in <c>Trips.Integrations</c> overrides this.
/// </summary>
internal sealed class StubTfNswClient : ITfNswClient
{
    public Task<TfNswTripPlan> TripPlanAsync(Point origin, Point destination, DateTimeOffset departAt, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(origin);
        ArgumentNullException.ThrowIfNull(destination);

        // Deterministic synthetic 3-leg plan: walk to a "boarding hub" 25% along the line, train
        // to an "alighting hub" 80% along, walk the rest. This matches the corridor candidate-node
        // generator's expectation that PT-leg endpoints are pickup-hub candidates — without two
        // distinct PT endpoints, the stub-driven dev/test flow produces a degenerate candidate
        // set with only Home + destination.
        var factory = new GeometryFactory(new PrecisionModel(), 4326);
        Point At(double t) => factory.CreatePoint(new Coordinate(
            origin.X + (destination.X - origin.X) * t,
            origin.Y + (destination.Y - origin.Y) * t));

        var boarding = At(0.25);
        var alighting = At(0.80);

        // Walks to/from a station are short in real life regardless of trip length — capped so
        // realistic 5-min walk budgets don't filter every candidate out on long trips. The PT
        // leg absorbs the distance.
        var distanceMetres = origin.Distance(destination) * 111_000.0;
        var walkToBoarding = 3;
        var trainMins = Math.Max(1, (int)Math.Round(distanceMetres * 0.75 / 400.0));
        var walkFromAlighting = 4;

        var legs = new[]
        {
            new TfNswJourneyLeg("walk", walkToBoarding, origin, boarding, RouteShortName: null,
                FromName: "Home", ToName: "Stub Boarding Hub"),
            new TfNswJourneyLeg("train", trainMins, boarding, alighting, RouteShortName: "T1",
                FromName: "Stub Boarding Hub", ToName: "Stub Alighting Hub"),
            new TfNswJourneyLeg("walk", walkFromAlighting, alighting, destination, RouteShortName: null,
                FromName: "Stub Alighting Hub", ToName: "Destination"),
        };
        return Task.FromResult(new TfNswTripPlan(legs,
            TotalWalkMins: walkToBoarding + walkFromAlighting,
            TotalPtMins: trainMins));
    }

    /// <summary>Real Sydney transit hubs hard-coded so the dev/demo experience without a TfNSW
    /// API key still produces a sensible candidate set: multiple passengers from different parts
    /// of Sydney will see the same big interchanges (Central, Chatswood, Parramatta, …) as
    /// candidates, which is what makes PT consolidation work in the solver.</summary>
    private static readonly (string StopId, string Name, double Lng, double Lat, string Mode)[] SydneyHubs =
    {
        // CBD / inner
        ("syd-central",        "Central Station",       151.2070, -33.8830, "train"),
        ("syd-town-hall",      "Town Hall",             151.2069, -33.8743, "train"),
        ("syd-wynyard",        "Wynyard",               151.2068, -33.8665, "train"),
        ("syd-circular-quay",  "Circular Quay",         151.2110, -33.8615, "train"),
        ("syd-redfern",        "Redfern",               151.1988, -33.8917, "train"),
        // North
        ("syd-north-sydney",   "North Sydney",          151.2073, -33.8404, "train"),
        ("syd-chatswood",      "Chatswood",             151.1804, -33.7969, "train"),
        ("syd-hornsby",        "Hornsby",               151.0993, -33.7029, "train"),
        ("syd-epping",         "Epping",                151.0822, -33.7720, "train"),
        ("syd-macquarie-park", "Macquarie Park",        151.1129, -33.7799, "train"),
        ("syd-eastwood",       "Eastwood",              151.0815, -33.7898, "train"),
        // West
        ("syd-parramatta",     "Parramatta",            151.0049, -33.8174, "train"),
        ("syd-strathfield",    "Strathfield",           151.0928, -33.8716, "train"),
        ("syd-top-ryde",       "Top Ryde Bus",          151.1078, -33.8062, "bus"),
        // South / East
        ("syd-hurstville",     "Hurstville",            151.1018, -33.9676, "train"),
        ("syd-bondi-jct",      "Bondi Junction",        151.2503, -33.8918, "train"),
        ("syd-airport",        "Domestic Airport",      151.1772, -33.9347, "train"),
        // Light rail
        ("syd-randwick",       "Randwick (light rail)", 151.2417, -33.9173, "light_rail"),
        // Ferry
        ("syd-manly-wharf",    "Manly Wharf",           151.2862, -33.7972, "ferry"),
    };

    public Task<IReadOnlyList<TfNswCoordinateStop>> CoordinateRequestAsync(Point origin, int radiusMeters, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(origin);
        var factory = new GeometryFactory(new PrecisionModel(), 4326);

        var hits = new List<TfNswCoordinateStop>();
        // Filter the hub table by haversine distance from the requested origin so the response
        // matches what a real TfNSW coord-request would do.
        foreach (var hub in SydneyHubs)
        {
            var d = (int)Math.Round(HaversineMetres(origin.Y, origin.X, hub.Lat, hub.Lng));
            if (d > radiusMeters) continue;
            hits.Add(new TfNswCoordinateStop(
                StopId: hub.StopId,
                Name: hub.Name,
                Location: factory.CreatePoint(new Coordinate(hub.Lng, hub.Lat)),
                DistanceMeters: d,
                Mode: hub.Mode));
        }

        // Plus a synthetic short walk-to-bus stop right next to home so even origins far from any
        // listed hub still get a walkable pickup option in dev.
        hits.Add(new TfNswCoordinateStop(
            StopId: "stub-local-bus",
            Name: "Local Bus Stop",
            Location: factory.CreatePoint(new Coordinate(origin.X + 0.001, origin.Y + 0.001)),
            DistanceMeters: 150,
            Mode: "bus"));

        IReadOnlyList<TfNswCoordinateStop> sorted = hits
            .OrderBy(s => s.DistanceMeters)
            .ToList();
        return Task.FromResult(sorted);
    }

    private static double HaversineMetres(double lat1Deg, double lng1Deg, double lat2Deg, double lng2Deg)
    {
        const double earthMetres = 6_371_008.8;
        var lat1 = lat1Deg * Math.PI / 180.0;
        var lat2 = lat2Deg * Math.PI / 180.0;
        var dLat = (lat2Deg - lat1Deg) * Math.PI / 180.0;
        var dLng = (lng2Deg - lng1Deg) * Math.PI / 180.0;
        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLng / 2) * Math.Sin(dLng / 2);
        return earthMetres * 2 * Math.Atan2(Math.Sqrt(h), Math.Sqrt(1 - h));
    }

    public Task<IReadOnlyList<TfNswDeparture>> DepartureAsync(string stopId, DateTimeOffset @from, CancellationToken ct) =>
        Task.FromResult<IReadOnlyList<TfNswDeparture>>(Array.Empty<TfNswDeparture>());

    public async IAsyncEnumerable<TfNswGtfsTripUpdate> GtfsRtTripUpdatesAsync(
        string mode,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        await Task.Yield();
        yield break;
    }
}
