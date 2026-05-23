using Microsoft.AspNetCore.Http.HttpResults;
using Trips.Api.Auth;
using Trips.Api.Mapping;
using Trips.Api.Optimisation;
using Trips.Api.Validation;
using Trips.Core.Abstractions;
using Trips.Core.Contracts;
using Trips.Core.Domain;

namespace Trips.Api.Endpoints;

public static class AdvancedEndpoints
{
    public static IEndpointRouteBuilder MapAdvanced(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var group = app.MapGroup("/trips/{tripId:guid}").WithTags("Advanced").RequireAuthorization();

        group.MapPost("/lock-solution", LockSolutionAsync)
            .AddEndpointFilter<ValidationFilter<LockSolutionRequest>>()
            .WithName("LockSolution");

        group.MapPost("/whatif", WhatIfAsync)
            .AddEndpointFilter<ValidationFilter<WhatIfRequest>>()
            .WithName("WhatIf");

        group.MapGet("/cost-split", CostSplitAsync).WithName("CostSplit");
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
        CurrentUser currentUser,
        IClock clock,
        HttpContext http,
        CancellationToken ct)
    {
        var trip = await authz.AuthorizeAsync(tripId, currentUser.UserIdGuid, ct).ConfigureAwait(false);
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
            actorId: currentUser.UserIdGuid,
            location: null,
            timestamp: clock.UtcNow), ct).ConfigureAwait(false);
        await trips.SaveChangesAsync(ct).ConfigureAwait(false);

        return TypedResults.Ok(trip.ToDto());
    }

    private static async Task<Results<Accepted<EnqueueRunResponse>, NotFound>> WhatIfAsync(
        Guid tripId,
        WhatIfRequest request,
        TripAuthorizationService authz,
        ITripRepository trips,
        IOptimisationRunRepository runs,
        IOptimisationJobQueue queue,
        CurrentUser currentUser,
        IClock clock,
        CancellationToken ct)
    {
        var trip = await authz.AuthorizeAsync(tripId, currentUser.UserIdGuid, ct).ConfigureAwait(false);
        if (trip is null)
        {
            return TypedResults.NotFound();
        }

        // Use latest locked solution's weights if available, otherwise the supplied weights, otherwise Balanced.
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

    private static async Task<Results<Ok<CostSplitResponse>, NotFound>> CostSplitAsync(
        Guid tripId,
        TripAuthorizationService authz,
        ITripRepository trips,
        IParticipantRepository participants,
        CurrentUser currentUser,
        CancellationToken ct)
    {
        var trip = await authz.AuthorizeAsync(tripId, currentUser.UserIdGuid, ct).ConfigureAwait(false);
        if (trip is null)
        {
            return TypedResults.NotFound();
        }

        var people = await participants.ListForTripAsync(tripId, ct).ConfigureAwait(false);
        var entries = people
            .Select(p => new CostSplitEntry(p.Id, p.DisplayName, Share: 0.0, Kilometres: 0.0))
            .ToList();

        var payload = new CostSplitResponse(
            TripId: tripId,
            SolutionId: trip.LockedSolutionId,
            Entries: entries,
            TotalCost: 0.0,
            Todo: "WS7 will populate fuel + tolls split by passenger-distance carried.");

        return TypedResults.Ok(payload);
    }

    private static async Task<Results<Accepted<EnqueueRunResponse>, NotFound>> ReturnLegAsync(
        Guid tripId,
        TripAuthorizationService authz,
        ITripRepository trips,
        IOptimisationRunRepository runs,
        IOptimisationJobQueue queue,
        CurrentUser currentUser,
        IClock clock,
        CancellationToken ct)
    {
        var trip = await authz.AuthorizeAsync(tripId, currentUser.UserIdGuid, ct).ConfigureAwait(false);
        if (trip is null)
        {
            return TypedResults.NotFound();
        }

        // Placeholder until WS7: enqueue a fresh balanced-weights run that the WS3 solver can
        // later wire to the clustered return-trip pipeline.
        var weights = ObjectiveWeights.Balanced;
        var run = new OptimisationRun(
            id: Guid.NewGuid(),
            tripId: tripId,
            weights: weights,
            solver: SolverKind.OrTools,
            startedAt: clock.UtcNow);

        await runs.AddAsync(run, ct).ConfigureAwait(false);
        await runs.SaveChangesAsync(ct).ConfigureAwait(false);
        await queue.EnqueueAsync(tripId, run.Id, weights, SolverKind.OrTools, repairHint: false, ct).ConfigureAwait(false);

        var location = $"/trips/{tripId}/runs/{run.Id}";
        return TypedResults.Accepted(location, new EnqueueRunResponse(run.Id));
    }

    private static IDictionary<string, object?> Trace(HttpContext http) =>
        new Dictionary<string, object?> { ["traceId"] = http.TraceIdentifier };
}
