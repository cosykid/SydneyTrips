using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using NetTopologySuite.Geometries;
using Trips.Api.Auth;
using Trips.Api.Mapping;
using Trips.Api.Optimisation;
using Trips.Api.Validation;
using Trips.Core.Abstractions;
using Trips.Core.Contracts;
using Trips.Core.Domain;
using Trips.Data;
using Trips.Optimisation.Cost;
using Trips.Optimisation.ReturnTrip;

namespace Trips.Api.Endpoints;

public static class AdvancedEndpoints
{
    /// <summary>Defaults read from the <c>Cost</c> config block when the caller doesn't override.</summary>
    public const double DefaultFuelPricePerLitre = 2.10;
    public const double DefaultFuelEconomyLPer100Km = 8.5;

    public static IEndpointRouteBuilder MapAdvanced(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var group = app.MapGroup("/trips/{tripId:guid}").WithTags("Advanced");

        group.MapPost("/lock-solution", LockSolutionAsync)
            .AddEndpointFilter<ValidationFilter<LockSolutionRequest>>()
            .WithName("LockSolution");

        group.MapGet("/locked-solution", GetLockedSolutionAsync).WithName("GetLockedSolution");

        group.MapPost("/whatif", WhatIfAsync)
            .AddEndpointFilter<ValidationFilter<WhatIfRequest>>()
            .WithName("WhatIf");

        // GET /cost-split for defaults (no body); POST /cost-split when the caller wants to override
        // fuel price/economy or supply tolls. We expose both verbs sharing the same handler so the
        // client can pick the one that's convenient.
        group.MapGet("/cost-split", (Guid tripId, TripAuthorizationService authz, ITripRepository trips,
            IParticipantRepository participants, ISolutionRepository solutions, ICostSplitService cost,
            IConfiguration cfg, HttpContext http, CancellationToken ct) =>
                CostSplitAsync(tripId, request: null, authz, trips, participants, solutions, cost, cfg, http, ct))
            .WithName("CostSplit");
        group.MapPost("/cost-split", CostSplitAsync).WithName("CostSplitWithInputs");

        group.MapPost("/return-leg", ReturnLegAsync).WithName("ReturnLeg");

        return app;
    }

    private static async Task<Results<Ok<TripDto>, NotFound, ProblemHttpResult>> LockSolutionAsync(
        Guid tripId,
        LockSolutionRequest request,
        TripAuthorizationService authz,
        ITripRepository trips,
        IOptimisationRunRepository runs,
        ITripEventRepository events,
        CurrentSession session,
        IClock clock,
        HttpContext http,
        CancellationToken ct)
    {
        var trip = await authz.LookupAsync(tripId, ct).ConfigureAwait(false);
        if (trip is null)
        {
            return TypedResults.NotFound();
        }

        var run = await runs.GetWithSolutionsAsync(request.RunId, ct).ConfigureAwait(false);
        if (run is null || run.TripId != tripId)
        {
            return TypedResults.NotFound();
        }

        var solutions = run.Solutions.OrderBy(s => s.Id).ToList();
        if (solutions.Count == 0 || request.ParetoIndex >= solutions.Count)
        {
            return TypedResults.Problem(
                detail: "The supplied paretoIndex does not match any solution for the run.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Invalid pareto index",
                extensions: Trace(http));
        }

        var chosen = solutions[request.ParetoIndex];
        trip.LockSolution(chosen.Id);
        await events.AddAsync(new TripEvent(
            id: Guid.NewGuid(),
            tripId: tripId,
            kind: EventKind.SolutionLocked,
            actorId: session.SessionId,
            location: null,
            timestamp: clock.UtcNow), ct).ConfigureAwait(false);
        await trips.SaveChangesAsync(ct).ConfigureAwait(false);

        // Re-fetch with participants loaded so ParticipantCount in the response is accurate
        // (Lookup returned the bare entity).
        var reloaded = await trips.GetWithParticipantsAsync(tripId, ct).ConfigureAwait(false) ?? trip;
        return TypedResults.Ok(reloaded.ToDto());
    }

    private static async Task<Results<Ok<SolutionDto>, NotFound>> GetLockedSolutionAsync(
        Guid tripId,
        TripAuthorizationService authz,
        ISolutionRepository solutions,
        CancellationToken ct)
    {
        var trip = await authz.LookupAsync(tripId, ct).ConfigureAwait(false);
        if (trip is null || trip.LockedSolutionId is null)
        {
            return TypedResults.NotFound();
        }
        var solution = await solutions.GetByIdAsync(trip.LockedSolutionId.Value, ct).ConfigureAwait(false);
        if (solution is null)
        {
            return TypedResults.NotFound();
        }
        return TypedResults.Ok(solution.ToDto());
    }

    private static async Task<Results<Accepted<EnqueueRunResponse>, NotFound, ProblemHttpResult>> WhatIfAsync(
        Guid tripId,
        WhatIfRequest request,
        TripAuthorizationService authz,
        ITripRepository trips,
        IOptimisationRunRepository runs,
        IOptimisationJobQueue queue,
        IClock clock,
        HttpContext http,
        CancellationToken ct)
    {
        var trip = await authz.LookupAsync(tripId, ct).ConfigureAwait(false);
        if (trip is null)
        {
            return TypedResults.NotFound();
        }
        if (trip.LockedSolutionId is null)
        {
            return TypedResults.Problem(
                detail: "What-if requires a locked solution to re-optimise from.",
                statusCode: StatusCodes.Status409Conflict,
                title: "No locked solution",
                extensions: Trace(http));
        }

        // The actual delta application + warm-start solve runs inside the optimisation worker; the
        // endpoint records the run with the requested weights (or the original run's if not supplied)
        // and the worker reconstructs the SolverInput from the locked solution. We carry the delta
        // forward via repairHint=true so the worker knows to use the locked solution as a hint.
        var weights = request.NewWeights?.ToDomain() ?? ObjectiveWeights.Balanced;

        var run = new OptimisationRun(
            id: Guid.NewGuid(),
            tripId: tripId,
            weights: weights,
            solver: SolverKind.OrTools,
            startedAt: clock.UtcNow);

        await runs.AddAsync(run, ct).ConfigureAwait(false);
        await runs.SaveChangesAsync(ct).ConfigureAwait(false);
        await queue.EnqueueAsync(tripId, run.Id, weights, SolverKind.OrTools, repairHint: true, ct).ConfigureAwait(false);

        var location = $"/trips/{tripId}/runs/{run.Id}";
        return TypedResults.Accepted(location, new EnqueueRunResponse(run.Id));
    }

    private static async Task<Results<Ok<CostSplitResponse>, NotFound, ProblemHttpResult>> CostSplitAsync(
        Guid tripId,
        CostSplitInputsDto? request,
        TripAuthorizationService authz,
        ITripRepository trips,
        IParticipantRepository participants,
        ISolutionRepository solutions,
        ICostSplitService cost,
        IConfiguration cfg,
        HttpContext http,
        CancellationToken ct)
    {
        var trip = await authz.LookupAsync(tripId, ct).ConfigureAwait(false);
        if (trip is null)
        {
            return TypedResults.NotFound();
        }
        if (trip.LockedSolutionId is null)
        {
            return TypedResults.Problem(
                detail: "Cost split is only available after a solution has been locked.",
                statusCode: StatusCodes.Status409Conflict,
                title: "No locked solution",
                extensions: Trace(http));
        }

        var solution = await solutions.GetByIdAsync(trip.LockedSolutionId.Value, ct).ConfigureAwait(false);
        if (solution is null)
        {
            return TypedResults.NotFound();
        }

        // Resolve cost inputs: query/body override > config defaults > hard-coded fallback.
        var fuelPrice = request?.FuelPricePerLitre
                        ?? cfg.GetValue<double?>("Cost:FuelPricePerLitre")
                        ?? DefaultFuelPricePerLitre;
        var fuelEconomy = request?.FuelEconomyLPer100Km
                          ?? cfg.GetValue<double?>("Cost:FuelEconomyLPer100Km")
                          ?? DefaultFuelEconomyLPer100Km;
        var tolls = (request?.Tolls ?? Array.Empty<TollSegmentDto>())
            .Select(t => new TollSegment(t.FromStopId, t.ToStopId, t.Amount))
            .ToList();
        var inputs = new CostInputs(fuelPrice, fuelEconomy, tolls);

        var breakdown = CostSplitService.Compute(solution, inputs);
        var people = (await participants.ListForTripAsync(tripId, ct).ConfigureAwait(false))
            .ToDictionary(p => p.Id, p => p);

        var entries = breakdown.ShareByPassenger
            .Select(s => new CostSplitEntry(
                ParticipantId: s.ParticipantId,
                DisplayName: people.TryGetValue(s.ParticipantId, out var pp) ? pp.DisplayName : "(unknown)",
                FuelShare: s.FuelShare,
                TollShare: s.TollShare,
                Total: s.Total))
            .OrderByDescending(e => e.Total)
            .ToList();

        var payload = new CostSplitResponse(
            TripId: tripId,
            SolutionId: trip.LockedSolutionId,
            Entries: entries,
            TotalCost: breakdown.TotalFuelCost + breakdown.TotalTollCost,
            TotalFuel: breakdown.TotalFuelCost,
            TotalTolls: breakdown.TotalTollCost,
            FuelPricePerLitre: fuelPrice,
            FuelEconomyLPer100Km: fuelEconomy);

        return TypedResults.Ok(payload);
    }

    private static async Task<Results<Ok<ReturnLegResponse>, NotFound, BadRequest<string>>> ReturnLegAsync(
        Guid tripId,
        ReturnLegRequest? request,
        TripAuthorizationService authz,
        IReturnTripPlanner planner,
        CancellationToken ct)
    {
        var trip = await authz.LookupAsync(tripId, ct).ConfigureAwait(false);
        if (trip is null)
        {
            return TypedResults.NotFound();
        }

        var dtoRequests = request?.Requests ?? Array.Empty<ReturnRequestDto>();
        if (dtoRequests.Count == 0)
        {
            return TypedResults.BadRequest("At least one ReturnRequest is required.");
        }

        var domainRequests = dtoRequests.Select(r => new ReturnRequest(
            ParticipantId: r.ParticipantId,
            DesiredDeparture: r.DesiredDeparture,
            DesiredDropoff: new Point(r.DropoffLongitude, r.DropoffLatitude) { SRID = 4326 })).ToList();

        var solutions = await planner.PlanReturnAsync(tripId, domainRequests, ct).ConfigureAwait(false);
        var solutionDtos = solutions.Select(s => s.ToDto()).ToList();
        return TypedResults.Ok(new ReturnLegResponse(solutionDtos));
    }

    private static IDictionary<string, object?> Trace(HttpContext http) =>
        new Dictionary<string, object?> { ["traceId"] = http.TraceIdentifier };
}
