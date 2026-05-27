using Microsoft.AspNetCore.Http.HttpResults;
using Trips.Api.Auth;
using Trips.Api.Mapping;
using Trips.Api.Optimisation;
using Trips.Api.Validation;
using Trips.Core.Abstractions;
using Trips.Core.Contracts;
using Trips.Core.Domain;

namespace Trips.Api.Endpoints;

public static class OptimisationEndpoints
{
    public static IEndpointRouteBuilder MapOptimisation(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var group = app.MapGroup("/trips/{tripId:guid}").WithTags("Optimisation");

        group.MapPost("/optimise", OptimiseAsync)
            .AddEndpointFilter<ValidationFilter<OptimiseRequest>>()
            .WithName("Optimise");

        group.MapGet("/runs/{runId:guid}", GetRunAsync).WithName("GetRun");
        group.MapGet("/runs/{runId:guid}/pareto", GetParetoAsync).WithName("GetParetoSolutions");

        return app;
    }

    private static async Task<Results<Accepted<EnqueueRunResponse>, NotFound>> OptimiseAsync(
        Guid tripId,
        OptimiseRequest request,
        TripAuthorizationService authz,
        IOptimisationRunRepository runs,
        IOptimisationJobQueue queue,
        IClock clock,
        CancellationToken ct)
    {
        var trip = await authz.LookupAsync(tripId, ct).ConfigureAwait(false);
        if (trip is null)
        {
            return TypedResults.NotFound();
        }

        var weights = request.Weights.ToDomain();
        var run = new OptimisationRun(
            id: Guid.NewGuid(),
            tripId: tripId,
            weights: weights,
            solver: request.Solver,
            startedAt: clock.UtcNow);

        await runs.AddAsync(run, ct).ConfigureAwait(false);
        await runs.SaveChangesAsync(ct).ConfigureAwait(false);
        await queue.EnqueueAsync(tripId, run.Id, weights, request.Solver, repairHint: false, ct).ConfigureAwait(false);

        var location = $"/trips/{tripId}/runs/{run.Id}";
        return TypedResults.Accepted(location, new EnqueueRunResponse(run.Id));
    }

    private static async Task<Results<Ok<OptimisationRunDtoWithSolution>, NotFound>> GetRunAsync(
        Guid tripId,
        Guid runId,
        TripAuthorizationService authz,
        IOptimisationRunRepository runs,
        ITripRepository trips,
        CancellationToken ct)
    {
        var trip = await authz.LookupAsync(tripId, ct).ConfigureAwait(false);
        if (trip is null)
        {
            return TypedResults.NotFound();
        }

        var run = await runs.GetWithSolutionsAsync(runId, ct).ConfigureAwait(false);
        if (run is null || run.TripId != tripId)
        {
            return TypedResults.NotFound();
        }

        var bestSolution = run.BestSolutionId.HasValue
            ? run.Solutions.FirstOrDefault(s => s.Id == run.BestSolutionId.Value)
            : run.Solutions.FirstOrDefault();

        // Re-load with participants + candidate nodes so the solution DTO can carry the walk/PT
        // split per pickup. authz.LookupAsync only loads the trip summary, not the participant set.
        var tripWithPeople = await trips.GetWithParticipantsAsync(tripId, ct).ConfigureAwait(false);
        var payload = new OptimisationRunDtoWithSolution(run.ToDto(), bestSolution?.ToDto(tripWithPeople));
        return TypedResults.Ok(payload);
    }

    private static async Task<Results<Ok<IReadOnlyList<SolutionDto>>, NotFound>> GetParetoAsync(
        Guid tripId,
        Guid runId,
        TripAuthorizationService authz,
        IOptimisationRunRepository runs,
        ITripRepository trips,
        ISolver solver,
        CancellationToken ct)
    {
        var trip = await authz.LookupAsync(tripId, ct).ConfigureAwait(false);
        if (trip is null)
        {
            return TypedResults.NotFound();
        }

        var run = await runs.GetWithSolutionsAsync(runId, ct).ConfigureAwait(false);
        if (run is null || run.TripId != tripId)
        {
            return TypedResults.NotFound();
        }

        var tripWithPeople = await trips.GetWithParticipantsAsync(tripId, ct).ConfigureAwait(false);
        if (tripWithPeople is null)
        {
            return TypedResults.NotFound();
        }

        // Re-solve with three weight vectors for the Pareto carousel.
        var presets = new (string Label, ObjectiveWeights Weights)[]
        {
            ("fastest", ObjectiveWeights.Fastest),
            ("fewest-stops", ObjectiveWeights.FewestStops),
            ("least-transit", ObjectiveWeights.LeastTransit),
        };

        var results = new List<SolutionDto>(capacity: presets.Length);
        foreach (var (label, weights) in presets)
        {
            var input = ParetoSupport.BuildSolverInput(tripWithPeople, runId, weights);
            var solution = await solver.SolveAsync(input, ct).ConfigureAwait(false);
            var labelled = new Solution(
                id: Guid.NewGuid(),
                optimisationRunId: runId,
                label: label,
                objective: solution.Objective,
                objectiveTerms: solution.ObjectiveTerms,
                routes: solution.Routes);
            results.Add(labelled.ToDto(tripWithPeople));
        }

        IReadOnlyList<SolutionDto> payload = results;
        return TypedResults.Ok(payload);
    }
}

/// <summary>Composite payload for GET /trips/{id}/runs/{runId} — the run row plus the best solution.</summary>
public sealed record OptimisationRunDtoWithSolution(OptimisationRunDto Run, SolutionDto? Solution);

internal static class ParetoSupport
{
    /// <summary>
    /// Build a solver input for the Pareto carousel's three preset weights. We reuse
    /// <see cref="SolverInputBuilder.Build"/> (the same builder the runner uses) so all three
    /// solves see the same node layout — critically including the candidate-node pickups, not
    /// just participant homes. We synthesise a throwaway <see cref="OptimisationRun"/> as the
    /// builder's run-context; only its Id + Weights matter for SolverInput.
    /// </summary>
    public static SolverInput BuildSolverInput(Trip trip, Guid runId, ObjectiveWeights weights)
    {
        var run = new OptimisationRun(runId, trip.Id, weights, SolverKind.OrTools, DateTimeOffset.UtcNow);
        return SolverInputBuilder.Build(trip, run);
    }
}
