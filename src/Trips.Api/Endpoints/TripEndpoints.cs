using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Http.HttpResults;
using NetTopologySuite.Geometries;
using Trips.Api.Auth;
using Trips.Api.Mapping;
using Trips.Api.Validation;
using Trips.Core.Abstractions;
using Trips.Core.Contracts;
using Trips.Core.Domain;

namespace Trips.Api.Endpoints;

public static class TripEndpoints
{
    public static IEndpointRouteBuilder MapTrips(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var group = app.MapGroup("/trips").WithTags("Trips").RequireAuthorization();

        group.MapPost("/", CreateAsync)
            .AddEndpointFilter<ValidationFilter<CreateTripRequest>>()
            .WithName("CreateTrip");

        group.MapGet("/", ListAsync).WithName("ListTrips");
        group.MapGet("/{id:guid}", GetAsync).WithName("GetTrip");
        group.MapDelete("/{id:guid}", DeleteAsync).WithName("DeleteTrip");

        return app;
    }

    private static async Task<Results<Created<TripDto>, ProblemHttpResult>> CreateAsync(
        CreateTripRequest request,
        ITripRepository trips,
        ITripEventRepository events,
        CurrentUser currentUser,
        IClock clock,
        HttpContext http,
        CancellationToken ct)
    {
        if (currentUser.UserIdGuid == Guid.Empty)
        {
            return TypedResults.Problem("User context missing.", statusCode: StatusCodes.Status401Unauthorized, extensions: Trace(http));
        }

        var factory = new GeometryFactory(new PrecisionModel(), 4326);
        var dest = new Destination(
            request.DestinationName,
            factory.CreatePoint(new Coordinate(request.DestinationLongitude, request.DestinationLatitude)));

        var trip = new Trip(
            id: Guid.NewGuid(),
            name: request.Name,
            destination: dest,
            departAt: request.DepartAt,
            arrivalWindow: new ArrivalWindow(request.ArrivalWindowEarliest, request.ArrivalWindowLatest),
            ownerId: currentUser.UserIdGuid,
            createdAt: clock.UtcNow);

        await trips.AddAsync(trip, ct).ConfigureAwait(false);
        await events.AddAsync(new TripEvent(
            id: Guid.NewGuid(),
            tripId: trip.Id,
            kind: EventKind.TripCreated,
            actorId: currentUser.UserIdGuid,
            location: dest.Location,
            timestamp: clock.UtcNow), ct).ConfigureAwait(false);
        await trips.SaveChangesAsync(ct).ConfigureAwait(false);

        return TypedResults.Created($"/trips/{trip.Id}", trip.ToDto());
    }

    private static async Task<Ok<IReadOnlyList<TripDto>>> ListAsync(
        ITripRepository trips,
        CurrentUser currentUser,
        CancellationToken ct)
    {
        var rows = await trips.ListForOwnerAsync(currentUser.UserIdGuid, ct).ConfigureAwait(false);
        IReadOnlyList<TripDto> dtos = rows.Select(r => r.ToDto()).ToList();
        return TypedResults.Ok(dtos);
    }

    private static async Task<Results<Ok<TripDetailDto>, NotFound, ForbidHttpResult>> GetAsync(
        Guid id,
        TripAuthorizationService authz,
        ITripRepository trips,
        CurrentUser currentUser,
        CancellationToken ct)
    {
        var authorized = await authz.AuthorizeAsync(id, currentUser.UserIdGuid, ct).ConfigureAwait(false);
        if (authorized is null)
        {
            return TypedResults.NotFound();
        }
        // AuthorizeAsync only loads the trip itself — re-fetch with participants + candidate
        // nodes so the planner / driver / cost-split views render in one round-trip.
        var full = await trips.GetWithParticipantsAsync(id, ct).ConfigureAwait(false);
        if (full is null)
        {
            return TypedResults.NotFound();
        }
        return TypedResults.Ok(full.ToDetailDto());
    }

    private static async Task<Results<NoContent, NotFound>> DeleteAsync(
        Guid id,
        ITripRepository trips,
        CurrentUser currentUser,
        CancellationToken ct)
    {
        var trip = await trips.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (trip is null || trip.OwnerId != currentUser.UserIdGuid)
        {
            return TypedResults.NotFound();
        }
        trips.Remove(trip);
        await trips.SaveChangesAsync(ct).ConfigureAwait(false);
        return TypedResults.NoContent();
    }

    private static IDictionary<string, object?> Trace(HttpContext http) =>
        new Dictionary<string, object?> { ["traceId"] = http.TraceIdentifier };
}
