using System.Globalization;
using NetTopologySuite.Geometries;
using Trips.Core.Domain;

namespace Trips.Core.Abstractions;

/// <summary>
/// Constructs a <see cref="SolverInput"/> from a persisted <see cref="Trip"/> + <see cref="OptimisationRun"/>.
///
/// <para>This is shared by the cold-start path (<c>OptimisationRunner.ExecuteJobAsync</c>) and the
/// what-if / repair path (<c>LockedContextRepository.GetByIdAsync</c>) so they produce identical
/// solver inputs for the same trip — anything that lets passengers be picked up at a stop other than
/// their doorstep needs to be plumbed here (not duplicated per caller).</para>
///
/// <para>Node layout: <c>[driver-origins, deduplicated candidate nodes, destination]</c>.
/// The travel matrix is a coarse haversine estimate (≈50 km/h × 1.3 detour factor); for production
/// accuracy the runner can overwrite it with a Google Routes matrix before invoking the solver.</para>
/// </summary>
public static class SolverInputBuilder
{
    /// <summary>
    /// Build a <see cref="SolverInput"/> for a first-time or what-if run.
    /// </summary>
    /// <param name="trip">The trip with eager-loaded participants and their <see cref="CandidateNode"/>s.</param>
    /// <param name="run">The owning run row (used for ids + objective weights).</param>
    /// <param name="warmStartHint">Optional hint from a prior solution; null for a cold start.</param>
    public static SolverInput Build(Trip trip, OptimisationRun run, WarmStartHint? warmStartHint = null)
    {
        ArgumentNullException.ThrowIfNull(trip);
        ArgumentNullException.ThrowIfNull(run);

        var nodes = new List<SolverNode>();
        var drivers = new List<SolverDriver>();
        var passengers = new List<SolverPassenger>();

        // (1) Drivers and their origin nodes.
        foreach (var driver in trip.Participants.Where(p => p.HasCar))
        {
            var idx = nodes.Count;
            nodes.Add(new SolverNode(idx, NodeKind.Home, CandidateNodeId: null, Location: driver.Home));
            drivers.Add(new SolverDriver(driver.Id, idx, driver.Seats));
        }

        // (2) Build a deduplicated candidate-node table across passengers.
        //
        // Each participant carries their own <see cref="CandidateNode"/> rows even when two
        // passengers' rows refer to the same physical stop (per-participant candidate sets carry
        // per-participant walk/PT minutes). Keying by `cn.Id` would emit a separate SolverNode at
        // identical coordinates for each passenger — the OR-Tools solver then treats them as
        // distinct places and may visit both, producing co-located duplicate stops.
        //
        // Collapse by <see cref="CanonicalKey"/>:
        //   • <c>cn.ExternalId</c> when present (TfNSW stop_id; stable across participants), else
        //   • a ~10m lat/lng bucket (4dp) so home-only nodes still dedup if two passengers happen to
        //     share an address.
        //
        // `canonicalIdxByKey` maps the canonical key → SolverNode index (one-per-physical-stop).
        // `idxByCnId` maps every domain CandidateNode.Id → the same canonical SolverNode index, so
        // each passenger's candidate-index list resolves through it to the deduped node. The
        // SolverNode's `CandidateNodeId` carries one canonical participant's CN id; downstream
        // mappers re-resolve per-participant walk/PT mins by canonical key (see Mappers.ToDto).
        var canonicalIdxByKey = new Dictionary<string, int>(StringComparer.Ordinal);
        var idxByCnId = new Dictionary<Guid, int>();
        var passengerParticipants = trip.Participants.Where(p => !p.HasCar).ToList();
        foreach (var passenger in passengerParticipants)
        {
            foreach (var cn in passenger.CandidateNodes)
            {
                var key = CanonicalKey(cn);
                if (canonicalIdxByKey.TryGetValue(key, out var existingIdx))
                {
                    idxByCnId[cn.Id] = existingIdx;
                    continue;
                }
                var idx = nodes.Count;
                nodes.Add(new SolverNode(idx, cn.Kind, CandidateNodeId: cn.Id, Location: cn.Location));
                canonicalIdxByKey[key] = idx;
                idxByCnId[cn.Id] = idx;
            }
        }

        // (3) Passenger rows reference the candidate-node indices their participant carries.
        // If a passenger somehow has no candidate nodes (e.g. TfNSW failed and Home wasn't added,
        // or a legacy trip pre-dating candidate generation), we synthesise a Home node here so the
        // solver still has at least one feasible pickup — otherwise the passenger is unsolvable
        // and silently dropped, which is the legacy behaviour we don't want to regress to.
        foreach (var passenger in passengerParticipants)
        {
            var candIndices = new List<int>(passenger.CandidateNodes.Count);
            var walkPts = new List<int>(passenger.CandidateNodes.Count);

            if (passenger.CandidateNodes.Count == 0)
            {
                var idx = nodes.Count;
                nodes.Add(new SolverNode(idx, NodeKind.Home, CandidateNodeId: null, Location: passenger.Home));
                candIndices.Add(idx);
                walkPts.Add(0);
            }
            else
            {
                // A passenger's own list may collide on the canonical key (e.g. TfNSW returns two
                // rows for the same stop). Drop the second occurrence so the solver doesn't see a
                // duplicated index — keep the first per-passenger walk/PT mins.
                var seen = new HashSet<int>(passenger.CandidateNodes.Count);
                foreach (var cn in passenger.CandidateNodes)
                {
                    if (!idxByCnId.TryGetValue(cn.Id, out var idx)) continue;
                    if (!seen.Add(idx)) continue;
                    candIndices.Add(idx);
                    walkPts.Add(cn.TravelMins);
                }
            }

            if (candIndices.Count == 0) continue;
            passengers.Add(new SolverPassenger(passenger.Id, candIndices, walkPts));
        }

        // (4) Destination — a CandidateNodeId=null row that isn't a driver origin. The OR-Tools
        // solver uses exactly that signal (see OrToolsSolver.FindDestinationIndex) so we never need
        // an explicit field for it on SolverInput.
        var destIndex = nodes.Count;
        nodes.Add(new SolverNode(destIndex, NodeKind.TrainStation, CandidateNodeId: null, Location: trip.DestinationLocation));

        // (5) Fallback driver if the trip has none. Mirrors the historical behaviour of the runner
        // so demo / unit-test trips that forget to add a driver still produce a solution.
        if (drivers.Count == 0 && trip.Participants.Count > 0)
        {
            var idx = nodes.Count;
            nodes.Add(new SolverNode(idx, NodeKind.Home, null, trip.Participants[0].Home));
            drivers.Add(new SolverDriver(trip.Participants[0].Id, idx, Math.Max(1, trip.Participants.Count)));
        }

        // (6) Coarse haversine travel matrix. The solver only cares about relative magnitudes; the
        // matrix is a placeholder for the real Google-Routes matrix we may snap on in production.
        var n = nodes.Count;
        var matrix = new double[n, n];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
            {
                matrix[i, j] = i == j ? 0.0 : HaversineMins(nodes[i].Location, nodes[j].Location);
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
            WarmStartHint: warmStartHint);
    }

    /// <summary>
    /// Canonical identity for a <see cref="CandidateNode"/> across participants — used to dedup
    /// per-participant CN rows that refer to the same physical pickup point. Two CNs sharing this
    /// key collapse to one <see cref="SolverNode"/>.
    ///
    /// <para>Order matters: prefer the TfNSW external id (stable, ground-truth) before falling back
    /// to a coordinate bucket. The bucket is ~10m at Sydney latitudes (0.0001° ≈ 11.1m lat /
    /// 9.3m lon) — tight enough that two stops on the same corner collide but two distinct nearby
    /// stops don't.</para>
    /// </summary>
    public static string CanonicalKey(CandidateNode cn)
    {
        ArgumentNullException.ThrowIfNull(cn);
        if (!string.IsNullOrWhiteSpace(cn.ExternalId))
        {
            return "ext:" + cn.ExternalId;
        }
        var lat = Math.Round(cn.Location.Y, 4, MidpointRounding.AwayFromZero);
        var lon = Math.Round(cn.Location.X, 4, MidpointRounding.AwayFromZero);
        return string.Create(CultureInfo.InvariantCulture, $"geo:{lat:F4},{lon:F4}");
    }

    private static double HaversineMins(Point a, Point b)
    {
        const double earthKm = 6371.0088;
        var lat1 = a.Y * Math.PI / 180.0;
        var lat2 = b.Y * Math.PI / 180.0;
        var dLat = (b.Y - a.Y) * Math.PI / 180.0;
        var dLon = (b.X - a.X) * Math.PI / 180.0;
        var h = Math.Sin(dLat / 2) * Math.Sin(dLat / 2)
                + Math.Cos(lat1) * Math.Cos(lat2) * Math.Sin(dLon / 2) * Math.Sin(dLon / 2);
        var c = 2 * Math.Atan2(Math.Sqrt(h), Math.Sqrt(1 - h));
        // ~50 km/h average × 1.3 road-curve factor → ≈1.56 min/km.
        return earthKm * c * 1.56;
    }
}
