using System.Text.Json;
using NetTopologySuite.Geometries;
using Trips.Core.Abstractions;
using Trips.Core.Domain;

namespace Trips.Bench.Generator;

/// <summary>
/// Synthesises realistic Sydney <see cref="SolverInput"/> instances from a fixed seed.
///
/// <list type="bullet">
///   <item>Passenger origins are drawn from a Gaussian mixture over curated Sydney suburb centroids
///   (loaded from <c>bench/sydney-suburbs.json</c>). σ ≈ 1.5 km so the suburb cluster shape is
///   preserved.</item>
///   <item>Each passenger gets 2–4 candidate pickup nodes: their home plus 1–3 jittered
///   "public-transport stops" within a 2km radius. Walk minutes for the home is 0; PT nodes get
///   walk = Euclidean / 80 m·min⁻¹.</item>
///   <item>Driver origins are drawn from the same suburb centroid pool (different cluster) so
///   driver homes don't perfectly overlap with passenger homes.</item>
///   <item>The travel-time matrix is synthesised from haversine distance × a "congestion factor"
///   so the bench doesn't need real API calls. Flagged as synthetic in <see cref="InstanceMetadata"/>.</item>
/// </list>
/// </summary>
public sealed class InstanceGenerator
{
    private readonly SuburbAtlas _atlas;
    private const double SigmaKm = 1.5;          // suburb-centroid jitter
    private const double WalkSpeedMpm = 80;      // 80 m/min ≈ brisk walk
    private const double DrivingKmh = 35;        // mixed-arterial average for Sydney
    private const double CongestionMultiplier = 1.25; // average peak/off-peak blend
    private const double EarthRadiusKm = 6371.0;

    public InstanceGenerator(SuburbAtlas atlas)
    {
        _atlas = atlas ?? throw new ArgumentNullException(nameof(atlas));
    }

    public static InstanceGenerator LoadFromJson(string path)
    {
        var json = File.ReadAllText(path);
        var atlas = JsonSerializer.Deserialize<SuburbAtlas>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidOperationException($"Failed to deserialise suburb atlas at {path}.");
        return new InstanceGenerator(atlas);
    }

    public BenchInstance Generate(int passengerCount, int driverCount, int seed)
    {
        var rng = new Random(seed);
        var dest = _atlas.Destinations[rng.Next(_atlas.Destinations.Length)];
        var destPoint = ToPoint(dest);

        // Pick passenger origin clusters and a (different) set for drivers.
        var suburbCount = _atlas.Origins.Length;
        var passengerCluster = Enumerable.Range(0, suburbCount).OrderBy(_ => rng.Next()).Take(Math.Min(passengerCount, suburbCount)).ToArray();
        var driverCluster = Enumerable.Range(0, suburbCount).OrderBy(_ => rng.Next()).Take(driverCount).ToArray();

        var passengers = new List<SolverPassenger>();
        var nodes = new List<SolverNode>();
        var nodePoints = new List<Point>();   // parallel to nodes, used by bench reporters & postprocessor

        // Driver origins first — indices 0..driverCount-1. Seats: distribute so total ≥ passengers +
        // slack=2, ensuring the instance is feasible. If passenger demand exceeds what realistic
        // per-driver caps (max 7 ≈ minibus) allow, we raise the per-driver cap so the instance
        // stays feasible — the bench is about solver quality, not feasibility stress tests.
        var drivers = new List<SolverDriver>(driverCount);
        var minSeats = 2;
        var totalNeeded = passengerCount + 2; // headroom for repositioning during search
        var seatTarget = Math.Max(totalNeeded, driverCount * minSeats);
        var perDriverCap = Math.Max(7, (int)Math.Ceiling((double)seatTarget / driverCount) + 1);
        var seats = new int[driverCount];
        for (var d = 0; d < driverCount; d++) seats[d] = minSeats;
        var remainder = seatTarget - driverCount * minSeats;
        var safety = remainder * driverCount + 10; // hard guard against pathological loops
        while (remainder > 0 && safety-- > 0)
        {
            var idx = rng.Next(driverCount);
            if (seats[idx] < perDriverCap) { seats[idx]++; remainder--; }
        }
        for (var d = 0; d < driverCount; d++)
        {
            var c = _atlas.Origins[driverCluster[d]];
            var home = JitterAround(c, rng);
            var idx = nodes.Count;
            nodes.Add(new SolverNode(idx, NodeKind.Home, null, home));   // null candidateId for driver homes
            nodePoints.Add(home);
            drivers.Add(new SolverDriver(Guid.NewGuid(), idx, seats[d]));
        }

        // Passengers + candidate nodes
        for (var p = 0; p < passengerCount; p++)
        {
            var clusterCentroid = _atlas.Origins[passengerCluster[p % passengerCluster.Length]];
            var home = JitterAround(clusterCentroid, rng);
            // Home is candidate 0 with walk = 0
            var candidateIndices = new List<int>();
            var walks = new List<int>();
            var participantId = Guid.NewGuid();

            var homeIdx = nodes.Count;
            nodes.Add(new SolverNode(homeIdx, NodeKind.Home, Guid.NewGuid(), home));
            nodePoints.Add(home);
            candidateIndices.Add(homeIdx);
            walks.Add(0);

            // 1..3 PT stops within ~2 km
            var ptCount = rng.Next(1, 4);
            for (var k = 0; k < ptCount; k++)
            {
                var stopBearing = rng.NextDouble() * 2 * Math.PI;
                var stopDistKm = 0.3 + rng.NextDouble() * 1.5; // 300m..1.8km
                var stopPoint = OffsetKm(home, stopBearing, stopDistKm);
                var walkMins = (int)Math.Round(stopDistKm * 1000 / WalkSpeedMpm);
                if (walkMins > 12) continue; // walk-budget infeasible — skip
                var idx = nodes.Count;
                var kind = (NodeKind)(rng.Next(1, 4)); // TrainStation/BusStop/Wharf
                nodes.Add(new SolverNode(idx, kind, Guid.NewGuid(), stopPoint));
                nodePoints.Add(stopPoint);
                candidateIndices.Add(idx);
                walks.Add(walkMins);
            }
            passengers.Add(new SolverPassenger(participantId, candidateIndices, walks));
        }

        // Destination — last node
        var destIdx = nodes.Count;
        nodes.Add(new SolverNode(destIdx, NodeKind.TrainStation, null, destPoint));   // dest never has CandidateNodeId
        nodePoints.Add(destPoint);

        // Travel matrix — haversine × congestion
        var matrix = new double[nodes.Count, nodes.Count];
        for (var i = 0; i < nodes.Count; i++)
        {
            for (var j = i + 1; j < nodes.Count; j++)
            {
                var km = HaversineKm(nodePoints[i], nodePoints[j]);
                var mins = km / DrivingKmh * 60.0 * CongestionMultiplier;
                matrix[i, j] = mins;
                matrix[j, i] = mins;
            }
        }

        var weights = ObjectiveWeights.Balanced;
        var input = new SolverInput(
            RunId: Guid.NewGuid(),
            TripId: Guid.NewGuid(),
            Weights: weights,
            Drivers: drivers,
            Passengers: passengers,
            Nodes: nodes,
            TravelMatrix: matrix,
            DepartAt: new DateTimeOffset(2026, 5, 23, 8, 0, 0, TimeSpan.FromHours(10)));

        var meta = new InstanceMetadata(
            Seed: seed,
            PassengerCount: passengerCount,
            DriverCount: driverCount,
            DestinationName: dest.Name,
            DestinationLocation: destPoint,
            IsSyntheticMatrix: true,
            CongestionMultiplier: CongestionMultiplier,
            NodePoints: nodePoints);

        return new BenchInstance(input, meta);
    }

    private static Point JitterAround(SuburbCentroid c, Random rng)
    {
        // Gaussian jitter in km
        var dxKm = SampleGaussian(rng) * SigmaKm;
        var dyKm = SampleGaussian(rng) * SigmaKm;
        var center = new Point(c.Lng, c.Lat) { SRID = 4326 };
        return OffsetKm(center, Math.Atan2(dyKm, dxKm), Math.Sqrt(dxKm * dxKm + dyKm * dyKm));
    }

    private static Point ToPoint(Destination d) => new(d.Lng, d.Lat) { SRID = 4326 };

    private static double SampleGaussian(Random rng)
    {
        // Box-Muller
        var u1 = 1.0 - rng.NextDouble();
        var u2 = 1.0 - rng.NextDouble();
        return Math.Sqrt(-2.0 * Math.Log(u1)) * Math.Cos(2.0 * Math.PI * u2);
    }

    /// <summary>Offset a WGS84 point by <paramref name="distanceKm"/> in <paramref name="bearingRad"/>.</summary>
    public static Point OffsetKm(Point origin, double bearingRad, double distanceKm)
    {
        var lat1 = origin.Y * Math.PI / 180.0;
        var lon1 = origin.X * Math.PI / 180.0;
        var d = distanceKm / EarthRadiusKm;
        var lat2 = Math.Asin(Math.Sin(lat1) * Math.Cos(d) + Math.Cos(lat1) * Math.Sin(d) * Math.Cos(bearingRad));
        var lon2 = lon1 + Math.Atan2(
            Math.Sin(bearingRad) * Math.Sin(d) * Math.Cos(lat1),
            Math.Cos(d) - Math.Sin(lat1) * Math.Sin(lat2));
        return new Point(lon2 * 180.0 / Math.PI, lat2 * 180.0 / Math.PI) { SRID = 4326 };
    }

    public static double HaversineKm(Point a, Point b)
    {
        var phi1 = a.Y * Math.PI / 180.0;
        var phi2 = b.Y * Math.PI / 180.0;
        var dphi = (b.Y - a.Y) * Math.PI / 180.0;
        var dlam = (b.X - a.X) * Math.PI / 180.0;
        var h = Math.Sin(dphi / 2) * Math.Sin(dphi / 2) + Math.Cos(phi1) * Math.Cos(phi2) * Math.Sin(dlam / 2) * Math.Sin(dlam / 2);
        return 2 * EarthRadiusKm * Math.Asin(Math.Sqrt(h));
    }
}

/// <summary>Suburb centroid pool deserialised from <c>sydney-suburbs.json</c>.</summary>
public sealed record SuburbAtlas(SuburbCentroid[] Origins, Destination[] Destinations);

/// <summary>One suburb centroid: name + lat/lng. The Y in NetTopologySuite is latitude; we keep
/// lat/lng separate here for JSON readability.</summary>
public sealed record SuburbCentroid(string Name, double Lat, double Lng);

/// <summary>One destination centroid: name + lat/lng. Same JSON convention as <see cref="SuburbCentroid"/>.</summary>
public sealed record Destination(string Name, double Lat, double Lng);

/// <summary>A generated instance: <see cref="SolverInput"/> plus generation metadata used by the
/// bench report and the post-processor.</summary>
public sealed record BenchInstance(SolverInput Input, InstanceMetadata Metadata);

/// <summary>Generation metadata: seed, sizes, destination name, the per-node geographic points
/// (for the post-processor and the map visualisations in the bench report), and a flag noting
/// the travel matrix is synthetic.</summary>
public sealed record InstanceMetadata(
    int Seed,
    int PassengerCount,
    int DriverCount,
    string DestinationName,
    Point DestinationLocation,
    bool IsSyntheticMatrix,
    double CongestionMultiplier,
    IReadOnlyList<Point> NodePoints)
{
    public string ClassLabel => $"{PassengerCount}p/{DriverCount}d";
}
