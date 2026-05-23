using Microsoft.EntityFrameworkCore;
using Trips.Core.Abstractions;
using Trips.Core.Domain;

namespace Trips.Data.Repositories;

/// <summary>
/// Builds a <see cref="LockedContext"/> for what-if re-optimisation: the locked <see cref="Solution"/>
/// plus a reconstructed <see cref="SolverInput"/> consistent with the original run.
///
/// <para>We don't persist <see cref="SolverInput"/> itself (it's a derived value of the trip + run),
/// so this repository rebuilds it from the trip's participants and the run's weights. The candidate
/// nodes are reconstructed from each participant's persisted <see cref="CandidateNode"/> set. The
/// travel matrix uses a coarse haversine-driven estimate; for production accuracy the matrix should
/// be re-fetched from <see cref="IGoogleRoutesClient"/> here, but that's an optimisation we defer to
/// the runner — the warm-start hint biases search regardless of matrix precision.</para>
/// </summary>
internal sealed class LockedContextRepository : ILockedContextRepository
{
    private readonly TripsDbContext _db;

    public LockedContextRepository(TripsDbContext db)
    {
        ArgumentNullException.ThrowIfNull(db);
        _db = db;
    }

    public async Task<LockedContext?> GetByIdAsync(Guid lockedSolutionId, CancellationToken ct)
    {
        var solution = await _db.Solutions
            .Include(s => s.Routes)
                .ThenInclude(r => r.Stops)
            .FirstOrDefaultAsync(s => s.Id == lockedSolutionId, ct).ConfigureAwait(false);
        if (solution is null) return null;

        var run = await _db.OptimisationRuns.FirstOrDefaultAsync(r => r.Id == solution.OptimisationRunId, ct)
            .ConfigureAwait(false);
        if (run is null) return null;

        var trip = await _db.Trips
            .Include(t => t.Participants)
                .ThenInclude(p => p.CandidateNodes)
            .FirstOrDefaultAsync(t => t.Id == run.TripId, ct).ConfigureAwait(false);
        if (trip is null) return null;

        var input = BuildSolverInput(trip, run);
        return new LockedContext(solution, input);
    }

    /// <summary>
    /// Reconstruct a <see cref="SolverInput"/> from a persisted trip + run. Node layout matches the
    /// runner's <c>BuildSolverInput</c> contract: drivers first, then a deduplicated candidate-node
    /// table sourced from participants' <see cref="CandidateNode"/> rows, then a destination row.
    /// </summary>
    public static SolverInput BuildSolverInput(Trip trip, OptimisationRun run)
    {
        var nodes = new List<SolverNode>();
        var drivers = new List<SolverDriver>();
        var passengers = new List<SolverPassenger>();

        // Drivers and their origin nodes.
        var driverParticipants = trip.Participants.Where(p => p.HasCar).ToList();
        foreach (var driver in driverParticipants)
        {
            var idx = nodes.Count;
            nodes.Add(new SolverNode(idx, NodeKind.Home, CandidateNodeId: null));
            drivers.Add(new SolverDriver(driver.Id, idx, driver.Seats));
        }

        // Build a candidate-node table — every distinct CandidateNode across all passengers.
        // CandidateNodeId == the domain CandidateNode.Id so the WhatIfService can re-stitch the
        // warm-start hint by matching ids.
        var candidateLookup = new Dictionary<Guid, int>();
        var passengerParticipants = trip.Participants.Where(p => !p.HasCar).ToList();
        foreach (var passenger in passengerParticipants)
        {
            foreach (var cn in passenger.CandidateNodes)
            {
                if (candidateLookup.ContainsKey(cn.Id)) continue;
                var idx = nodes.Count;
                nodes.Add(new SolverNode(idx, cn.Kind, CandidateNodeId: cn.Id));
                candidateLookup[cn.Id] = idx;
            }
        }

        // Passenger rows.
        foreach (var passenger in passengerParticipants)
        {
            var candIndices = new List<int>(passenger.CandidateNodes.Count);
            var walkPts = new List<int>(passenger.CandidateNodes.Count);
            foreach (var cn in passenger.CandidateNodes)
            {
                if (!candidateLookup.TryGetValue(cn.Id, out var idx)) continue;
                candIndices.Add(idx);
                walkPts.Add(cn.TravelMins);
            }
            if (candIndices.Count == 0) continue; // skip unsolvable passengers
            passengers.Add(new SolverPassenger(passenger.Id, candIndices, walkPts));
        }

        // Destination row.
        var destIndex = nodes.Count;
        nodes.Add(new SolverNode(destIndex, NodeKind.TrainStation, CandidateNodeId: null));

        // Synthesise a default driver if there are none (matches runner's fallback).
        if (drivers.Count == 0 && trip.Participants.Count > 0)
        {
            var idx = nodes.Count;
            nodes.Add(new SolverNode(idx, NodeKind.Home, null));
            drivers.Add(new SolverDriver(trip.Participants[0].Id, idx, Math.Max(1, trip.Participants.Count)));
        }

        // Build a coarse matrix. Distance between two participants' nodes is approximated by haversine
        // between the underlying CandidateNode (or participant home) WGS84 points.
        var n = nodes.Count;
        var matrix = new double[n, n];
        var locations = ResolveLocations(trip, nodes);
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
            {
                matrix[i, j] = i == j ? 0.0 : HaversineMins(locations[i], locations[j]);
            }
        }

        return new SolverInput(
            RunId: run.Id,
            TripId: trip.Id,
            Weights: run.Weights,
            Drivers: drivers,
            Passengers: passengers,
            Nodes: nodes,
            TravelMatrix: matrix,
            DepartAt: trip.DepartAt,
            WarmStartHint: null);
    }

    /// <summary>
    /// Map each <see cref="SolverNode"/> back to a WGS84 location for matrix synthesis.
    /// </summary>
    private static NetTopologySuite.Geometries.Point[] ResolveLocations(Trip trip, IReadOnlyList<SolverNode> nodes)
    {
        var driverHomes = trip.Participants.Where(p => p.HasCar).ToDictionary(p => p.Home, p => p);
        var candidateById = trip.Participants
            .SelectMany(p => p.CandidateNodes)
            .ToDictionary(cn => cn.Id, cn => cn.Location);
        var driverOrigins = trip.Participants.Where(p => p.HasCar).Select(p => p.Home).ToList();

        var result = new NetTopologySuite.Geometries.Point[nodes.Count];
        var driverCursor = 0;
        for (var i = 0; i < nodes.Count; i++)
        {
            var node = nodes[i];
            if (node.CandidateNodeId is { } cid && candidateById.TryGetValue(cid, out var pt))
            {
                result[i] = pt;
            }
            else if (driverCursor < driverOrigins.Count)
            {
                result[i] = driverOrigins[driverCursor++];
            }
            else
            {
                result[i] = trip.DestinationLocation;
            }
        }
        return result;
    }

    private static double HaversineMins(NetTopologySuite.Geometries.Point a, NetTopologySuite.Geometries.Point b)
    {
        const double earthKm = 6371.0088;
        var lat1 = a.Y * Math.PI / 180.0;
        var lat2 = b.Y * Math.PI / 180.0;
        var dLat = (b.Y - a.Y) * Math.PI / 180.0;
        var dLon = (b.X - a.X) * Math.PI / 180.0;
        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(h), Math.Sqrt(1 - h));
        // ~50 km/h average × 1.3 road-curve factor → ≈1.56 min/km
        return earthKm * c * 1.56;
    }
}
