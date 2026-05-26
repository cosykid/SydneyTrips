using FluentAssertions;
using NetTopologySuite.Geometries;
using Trips.Core.Abstractions;
using Trips.Integrations.Clients;

namespace Trips.Integrations.Tests;

/// <summary>
/// Verifies <see cref="HybridRoutesClient"/>'s dispatch: free-flow (planning) matrices go to the
/// OSRM free-flow source, traffic-aware (live ETA) matrices and polylines stay on Google, and with
/// no OSRM configured everything forwards to Google unchanged.
/// </summary>
public sealed class HybridRoutesClientTests
{
    private static readonly GeometryFactory Geom = new(new PrecisionModel(), 4326);
    private static readonly Point[] Pts = { Geom.CreatePoint(new Coordinate(151.0, -33.0)) };

    [Fact]
    public async Task Free_flow_matrix_is_served_by_osrm_not_google()
    {
        var google = new RecordingGoogle();
        var osrm = new RecordingFreeFlow();
        var hybrid = new HybridRoutesClient(google, osrm);

        await hybrid.ComputeRouteMatrixAsync(Pts, Pts, trafficAware: false, CancellationToken.None);

        osrm.Calls.Should().Be(1);
        google.MatrixCalls.Should().Be(0);
    }

    [Fact]
    public async Task Traffic_aware_matrix_is_served_by_google_not_osrm()
    {
        var google = new RecordingGoogle();
        var osrm = new RecordingFreeFlow();
        var hybrid = new HybridRoutesClient(google, osrm);

        await hybrid.ComputeRouteMatrixAsync(Pts, Pts, trafficAware: true, CancellationToken.None);

        google.MatrixCalls.Should().Be(1);
        google.LastTrafficAware.Should().BeTrue();
        osrm.Calls.Should().Be(0);
    }

    [Fact]
    public async Task Free_flow_falls_back_to_google_when_no_osrm_configured()
    {
        var google = new RecordingGoogle();
        var hybrid = new HybridRoutesClient(google, freeFlow: null);

        await hybrid.ComputeRouteMatrixAsync(Pts, Pts, trafficAware: false, CancellationToken.None);

        google.MatrixCalls.Should().Be(1);
        google.LastTrafficAware.Should().BeFalse();
    }

    [Fact]
    public async Task Compute_routes_always_goes_to_google()
    {
        var google = new RecordingGoogle();
        var hybrid = new HybridRoutesClient(google, new RecordingFreeFlow());

        await hybrid.ComputeRoutesAsync(Pts[0], Pts[0], Array.Empty<Point>(), optimizeWaypointOrder: false, CancellationToken.None);

        google.RouteCalls.Should().Be(1);
    }

    private sealed class RecordingGoogle : IGoogleRoutesClient
    {
        public int MatrixCalls { get; private set; }
        public int RouteCalls { get; private set; }
        public bool? LastTrafficAware { get; private set; }

        public Task<double[,]> ComputeRouteMatrixAsync(IReadOnlyList<Point> origins, IReadOnlyList<Point> destinations, bool trafficAware, CancellationToken ct)
        {
            MatrixCalls++;
            LastTrafficAware = trafficAware;
            return Task.FromResult(new double[origins.Count, destinations.Count]);
        }

        public Task<GoogleRoutesResult> ComputeRoutesAsync(Point origin, Point destination, IReadOnlyList<Point> waypoints, bool optimizeWaypointOrder, CancellationToken ct)
        {
            RouteCalls++;
            return Task.FromResult(new GoogleRoutesResult(Array.Empty<GoogleRouteLeg>(), 0, 0, null));
        }
    }

    private sealed class RecordingFreeFlow : IFreeFlowMatrixClient
    {
        public int Calls { get; private set; }

        public Task<double[,]> ComputeMatrixAsync(IReadOnlyList<Point> origins, IReadOnlyList<Point> destinations, CancellationToken ct)
        {
            Calls++;
            return Task.FromResult(new double[origins.Count, destinations.Count]);
        }
    }
}
