using Trips.Core.Abstractions;
using Trips.Core.Contracts;
using Trips.Core.Domain;

namespace Trips.Api.Mapping;

/// <summary>
/// Hand-written domain → DTO mappers. Kept explicit (no AutoMapper) so the contract is obvious
/// and we never accidentally serialise a private field.
/// </summary>
internal static class Mappers
{
    public static TripDto ToDto(this Trip trip)
    {
        ArgumentNullException.ThrowIfNull(trip);
        return new TripDto(
            Id: trip.Id,
            Name: trip.Name,
            DestinationName: trip.DestinationName,
            DestinationLongitude: trip.DestinationLocation.X,
            DestinationLatitude: trip.DestinationLocation.Y,
            DepartAt: trip.DepartAt,
            ArrivalWindowEarliest: trip.ArrivalWindowEarliest,
            ArrivalWindowLatest: trip.ArrivalWindowLatest,
            OwnerId: trip.OwnerId,
            CreatedAt: trip.CreatedAt,
            LockedSolutionId: trip.LockedSolutionId,
            ParticipantCount: trip.Participants.Count);
    }

    /// <summary>Mapper for GET /trips/{id} — assumes the trip was loaded via
    /// <see cref="ITripRepository.GetWithParticipantsAsync"/> so Participants and their
    /// CandidateNodes are populated.</summary>
    public static TripDetailDto ToDetailDto(this Trip trip)
    {
        ArgumentNullException.ThrowIfNull(trip);
        var participants = trip.Participants
            .Select(p => p.ToDetailDto())
            .ToList();
        return new TripDetailDto(
            Id: trip.Id,
            Name: trip.Name,
            DestinationName: trip.DestinationName,
            DestinationLongitude: trip.DestinationLocation.X,
            DestinationLatitude: trip.DestinationLocation.Y,
            DepartAt: trip.DepartAt,
            ArrivalWindowEarliest: trip.ArrivalWindowEarliest,
            ArrivalWindowLatest: trip.ArrivalWindowLatest,
            OwnerId: trip.OwnerId,
            CreatedAt: trip.CreatedAt,
            LockedSolutionId: trip.LockedSolutionId,
            Participants: participants);
    }

    public static ParticipantDto ToDto(this Participant p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return new ParticipantDto(
            Id: p.Id,
            TripId: p.TripId,
            DisplayName: p.DisplayName,
            HomeLongitude: p.Home.X,
            HomeLatitude: p.Home.Y,
            HasCar: p.HasCar,
            Seats: p.Seats,
            Preferences: new PreferencesDto(p.WalkBudgetMins, p.DetourToleranceMins, p.FairnessWeight));
    }

    public static ParticipantWithNodesDto ToDetailDto(this Participant p)
    {
        ArgumentNullException.ThrowIfNull(p);
        var nodes = p.CandidateNodes
            .Select(n => new CandidateNodeDto(
                Id: n.Id,
                ParticipantId: n.ParticipantId,
                Kind: (CandidateNodeKindDto)n.Kind,
                Longitude: n.Location.X,
                Latitude: n.Location.Y,
                WalkMins: n.WalkMins,
                PtMins: n.PtMins,
                ExternalId: n.ExternalId,
                DisplayName: n.DisplayName,
                Path: ToPathDto(n.Path)))
            .ToList();
        return new ParticipantWithNodesDto(
            Id: p.Id,
            TripId: p.TripId,
            DisplayName: p.DisplayName,
            HomeLongitude: p.Home.X,
            HomeLatitude: p.Home.Y,
            HasCar: p.HasCar,
            Seats: p.Seats,
            Preferences: new PreferencesDto(p.WalkBudgetMins, p.DetourToleranceMins, p.FairnessWeight),
            CandidateNodes: nodes);
    }

    public static OptimisationRunDto ToDto(this OptimisationRun run)
    {
        ArgumentNullException.ThrowIfNull(run);
        OptimisationStatsDto? stats = run.WallClock.HasValue
            ? new OptimisationStatsDto(
                WallClock: run.WallClock.Value,
                IterationsOrNodes: run.IterationsOrNodes ?? 0,
                BestObjective: run.BestObjective ?? 0.0,
                LpRelaxation: run.LpRelaxation,
                Solver: run.Solver)
            : null;

        return new OptimisationRunDto(
            Id: run.Id,
            TripId: run.TripId,
            Status: run.Status,
            Solver: run.Solver,
            Weights: new ObjectiveWeightsDto(
                run.WeightDriveTime,
                run.WeightStopCount,
                run.WeightWalkAndPt,
                run.WeightArrivalSpread,
                run.WeightFairness),
            StartedAt: run.StartedAt,
            CompletedAt: run.CompletedAt,
            FailureReason: run.FailureReason,
            BestSolutionId: run.BestSolutionId,
            Stats: stats);
    }

    /// <summary>
    /// Map a <see cref="Solution"/> to its DTO. Pass <paramref name="trip"/> (with participants and
    /// their candidate nodes eager-loaded) so each <see cref="StopDto.Pickups"/> entry can carry the
    /// passenger's walk/PT split. When <paramref name="trip"/> is null we fall back to zero mins
    /// for both — useful for endpoints that only have the solution at hand.
    /// </summary>
    public static SolutionDto ToDto(this Solution solution, Trip? trip = null)
    {
        ArgumentNullException.ThrowIfNull(solution);

        // SolverInputBuilder dedups co-located CandidateNodes across passengers (one canonical
        // SolverNode per physical stop). The resulting `Stop.CandidateNodeId` points at the
        // canonical participant's CN row — which may not be the picking passenger's own CN. We
        // therefore resolve walk/PT mins by canonical key (TfNSW stop_id, or ~10m lat/lng bucket)
        // rather than by CN id directly. In the non-deduped case this collapses to the old
        // behaviour because each participant's own CN canonicalises back to the same key.
        var keyByCnId = new Dictionary<Guid, string>();
        var kindByKey = new Dictionary<string, NodeKind>(StringComparer.Ordinal);
        var legsByPidAndKey = new Dictionary<(Guid Participant, string Key), (int Walk, int Pt)>();
        // Each passenger's own home→hub geometry, keyed the same way as the walk/PT split. Carried
        // per-leg so co-located passengers (who collapse to one canonical SolverNode) each keep
        // their own path instead of all inheriting the canonical node's.
        var pathByPidAndKey = new Dictionary<(Guid Participant, string Key), PathDto?>();
        if (trip is not null)
        {
            foreach (var participant in trip.Participants)
            {
                foreach (var cn in participant.CandidateNodes)
                {
                    var key = SolverInputBuilder.CanonicalKey(cn);
                    keyByCnId[cn.Id] = key;
                    kindByKey.TryAdd(key, cn.Kind);
                    legsByPidAndKey[(participant.Id, key)] = (cn.WalkMins, cn.PtMins);
                    pathByPidAndKey[(participant.Id, key)] = ToPathDto(cn.Path);
                }
            }
        }

        NodeKind ResolveNodeKind(Guid candidateNodeId)
        {
            if (trip is null || candidateNodeId == Guid.Empty) return NodeKind.Home;
            if (keyByCnId.TryGetValue(candidateNodeId, out var key)
                && kindByKey.TryGetValue(key, out var kind))
            {
                return kind;
            }
            return NodeKind.Home;
        }

        var routes = solution.Routes
            .OrderBy(r => r.OrderIndex)
            .Select(r => new DriverRouteDto(
                Id: r.Id,
                DriverId: r.DriverId,
                TravelMins: r.TravelMins,
                OrderIndex: r.OrderIndex,
                Stops: r.Stops
                    .OrderBy(s => s.OrderIndex)
                    .Select(s => new StopDto(
                        Id: s.Id,
                        OrderIndex: s.OrderIndex,
                        Longitude: s.Location.X,
                        Latitude: s.Location.Y,
                        CandidateNodeId: s.CandidateNodeId,
                        NodeKind: (CandidateNodeKindDto)ResolveNodeKind(s.CandidateNodeId),
                        EstimatedArrival: s.EstimatedArrival,
                        Pickups: s.Pickups
                            .Select(pid =>
                            {
                                if (keyByCnId.TryGetValue(s.CandidateNodeId, out var key))
                                {
                                    var legs = legsByPidAndKey.TryGetValue((pid, key), out var l) ? l : (Walk: 0, Pt: 0);
                                    pathByPidAndKey.TryGetValue((pid, key), out var path);
                                    return new PickupLegDto(pid, legs.Walk, legs.Pt, path);
                                }
                                return new PickupLegDto(pid, WalkMins: 0, PtMins: 0, Path: null);
                            })
                            .ToList()))
                    .ToList()))
            .ToList();

        return new SolutionDto(
            Id: solution.Id,
            OptimisationRunId: solution.OptimisationRunId,
            Label: solution.Label,
            Objective: solution.Objective,
            ObjectiveTerms: solution.ObjectiveTerms,
            Routes: routes);
    }

    public static ObjectiveWeights ToDomain(this ObjectiveWeightsDto dto)
    {
        ArgumentNullException.ThrowIfNull(dto);
        return new ObjectiveWeights(
            DriveTime: dto.DriveTime,
            StopCount: dto.StopCount,
            WalkAndPt: dto.WalkAndPt,
            ArrivalSpread: dto.ArrivalSpread,
            Fairness: dto.Fairness);
    }

    private static PathDto? ToPathDto(NetTopologySuite.Geometries.LineString? path)
    {
        if (path is null || path.NumPoints < 2) return null;
        var coords = new List<PathCoordinateDto>(path.NumPoints);
        for (var i = 0; i < path.NumPoints; i++)
        {
            var c = path.GetCoordinateN(i);
            coords.Add(new PathCoordinateDto(Longitude: c.X, Latitude: c.Y));
        }
        return new PathDto(coords);
    }
}
