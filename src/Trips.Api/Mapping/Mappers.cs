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
            LockedSolutionId: trip.LockedSolutionId);
    }

    public static ParticipantDto ToDto(this Participant p)
    {
        ArgumentNullException.ThrowIfNull(p);
        return new ParticipantDto(
            Id: p.Id,
            TripId: p.TripId,
            UserId: p.UserId,
            DisplayName: p.DisplayName,
            HomeLongitude: p.Home.X,
            HomeLatitude: p.Home.Y,
            HasCar: p.HasCar,
            Seats: p.Seats,
            Preferences: new PreferencesDto(p.WalkBudgetMins, p.DetourToleranceMins, p.FairnessWeight));
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

    public static SolutionDto ToDto(this Solution solution)
    {
        ArgumentNullException.ThrowIfNull(solution);
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
                        EstimatedArrival: s.EstimatedArrival,
                        Pickups: s.Pickups))
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
}
