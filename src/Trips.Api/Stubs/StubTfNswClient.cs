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
        var leg = new TfNswJourneyLeg("walk", 10, origin, destination, RouteShortName: null);
        return Task.FromResult(new TfNswTripPlan(new[] { leg }, TotalWalkMins: 10, TotalPtMins: 0));
    }

    public Task<IReadOnlyList<TfNswCoordinateStop>> CoordinateRequestAsync(Point origin, int radiusMeters, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(origin);
        var factory = new GeometryFactory(new PrecisionModel(), 4326);

        // Two fake stops within walking distance for synthetic candidate-node generation.
        IReadOnlyList<TfNswCoordinateStop> stops = new[]
        {
            new TfNswCoordinateStop(
                StopId: "stub-stop-1",
                Name: "Stub Train Station",
                Location: factory.CreatePoint(new Coordinate(origin.X + 0.001, origin.Y + 0.001)),
                DistanceMeters: 150,
                Mode: "train"),
            new TfNswCoordinateStop(
                StopId: "stub-stop-2",
                Name: "Stub Bus Stop",
                Location: factory.CreatePoint(new Coordinate(origin.X - 0.001, origin.Y + 0.0005)),
                DistanceMeters: 220,
                Mode: "bus"),
        };
        return Task.FromResult(stops);
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
