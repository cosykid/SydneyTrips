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
        var group = app.MapGroup("/trips/{tripId:guid}").WithTags("Optimisation").RequireAuthorization();

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
        CurrentUser currentUser,
        IClock clock,
        CancellationToken ct)
    {
        var trip = await authz.AuthorizeAsync(tripId, currentUser.UserIdGuid, ct).ConfigureAwait(false);
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
        CurrentUser currentUser,
        CancellationToken ct)
    {
        var trip = await authz.AuthorizeAsync(tripId, currentUser.UserIdGuid, ct).ConfigureAwait(false);
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

        var payload = new OptimisationRunDtoWithSolution(run.ToDto(), bestSolution?.ToDto());
        return TypedResults.Ok(payload);
    }

    private static async Task<Results<Ok<IReadOnlyList<SolutionDto>>, NotFound>> GetParetoAsync(
        Guid tripId,
        Guid runId,
        TripAuthorizationService authz,
        IOptimisationRunRepository runs,
        ITripRepository trips,
        ISolver solver,
        CurrentUser currentUser,
        CancellationToken ct)
    {
        var trip = await authz.AuthorizeAsync(tripId, currentUser.UserIdGuid, ct).ConfigureAwait(false);
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
            ("least-walking", ObjectiveWeights.LeastWalking),
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
            results.Add(labelled.ToDto());
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
    /// Tiny duplicate of <c>OptimisationRunner.BuildSolverInput</c> to avoid the runner having to
    /// expose internals. Real WS3 solvers ignore most of this in favour of their own pipelines.
    /// </summary>
    public static SolverInput BuildSolverInput(Trip trip, Guid runId, ObjectiveWeights weights)
    {
        var nodes = new List<SolverNode> { new(0, NodeKind.Home, null) };
        var drivers = new List<SolverDriver>();
        var passengers = new List<SolverPassenger>();
        var index = 1;
        foreach (var p in trip.Participants)
        {
            var idx = index++;
            nodes.Add(new SolverNode(idx, NodeKind.Home, null));
            if (p.HasCar)
            {
                drivers.Add(new SolverDriver(p.Id, idx, p.Seats));
            }
            else
            {
                passengers.Add(new SolverPassenger(p.Id, new[] { idx }, new[] { 0 }));
            }
        }
        if (drivers.Count == 0 && trip.Participants.Count > 0)
        {
            drivers.Add(new SolverDriver(trip.Participants[0].Id, 1, Math.Max(1, trip.Participants.Count)));
        }
        var n = nodes.Count;
        var matrix = new double[n, n];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++)
            {
                matrix[i, j] = i == j ? 0.0 : 10.0;
            }
        }
        return new SolverInput(runId, trip.Id, weights, drivers, passengers, nodes, matrix, trip.DepartAt);
    }
}
