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
        var group = app.MapGroup("/trips").WithTags("Trips");

        group.MapPost("/", CreateAsync)
            .AddEndpointFilter<ValidationFilter<CreateTripRequest>>()
            .WithName("CreateTrip");

        group.MapGet("/", ListAsync).WithName("ListTrips");
        group.MapGet("/{id:guid}", GetAsync).WithName("GetTrip");
        group.MapDelete("/{id:guid}", DeleteAsync).WithName("DeleteTrip");
        group.MapPatch("/{id:guid}/destination", UpdateDestinationAsync)
            .AddEndpointFilter<ValidationFilter<UpdateTripDestinationRequest>>()
            .WithName("UpdateTripDestination");

        return app;
    }

    private static async Task<Results<Ok<TripDto>, NotFound>> UpdateDestinationAsync(
        Guid id,
        UpdateTripDestinationRequest request,
        TripAuthorizationService authz,
        ITripRepository trips,
        CancellationToken ct)
    {
        var trip = await authz.LookupAsync(id, ct).ConfigureAwait(false);
        if (trip is null)
        {
            return TypedResults.NotFound();
        }

        var factory = new GeometryFactory(new PrecisionModel(), 4326);
        var dest = new Destination(
            request.DestinationName,
            factory.CreatePoint(new Coordinate(request.DestinationLongitude, request.DestinationLatitude)));
        trip.UpdateDestination(dest);
        await trips.SaveChangesAsync(ct).ConfigureAwait(false);

        return TypedResults.Ok(trip.ToDto());
    }

    private static async Task<Created<TripDto>> CreateAsync(
        CreateTripRequest request,
        ITripRepository trips,
        ITripEventRepository events,
        CurrentSession session,
        IClock clock,
        CancellationToken ct)
    {
        // The anonymous-session middleware guarantees a non-empty SessionId for any request
        // that actually traversed the pipeline; we don't reject Guid.Empty here because we'd
        // rather a fresh browser get its trip created with a fresh cookie than be told to retry.
        var ownerId = session.SessionId;

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
            ownerId: ownerId,
            createdAt: clock.UtcNow);

        await trips.AddAsync(trip, ct).ConfigureAwait(false);
        await events.AddAsync(new TripEvent(
            id: Guid.NewGuid(),
            tripId: trip.Id,
            kind: EventKind.TripCreated,
            actorId: ownerId,
            location: dest.Location,
            timestamp: clock.UtcNow), ct).ConfigureAwait(false);
        await trips.SaveChangesAsync(ct).ConfigureAwait(false);

        return TypedResults.Created($"/trips/{trip.Id}", trip.ToDto());
    }

    private static async Task<Ok<IReadOnlyList<TripDto>>> ListAsync(
        ITripRepository trips,
        CurrentSession session,
        CancellationToken ct)
    {
        var rows = await trips.ListForOwnerAsync(session.SessionId, ct).ConfigureAwait(false);
        IReadOnlyList<TripDto> dtos = rows.Select(r => r.ToDto()).ToList();
        return TypedResults.Ok(dtos);
    }

    private static async Task<Results<Ok<TripDetailDto>, NotFound>> GetAsync(
        Guid id,
        TripAuthorizationService authz,
        ITripRepository trips,
        CancellationToken ct)
    {
        var trip = await authz.LookupAsync(id, ct).ConfigureAwait(false);
        if (trip is null)
        {
            return TypedResults.NotFound();
        }
        // Lookup only loaded the trip itself — re-fetch with participants + candidate nodes
        // so the planner / driver / cost-split views render in one round-trip.
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
        CurrentSession session,
        CancellationToken ct)
    {
        // Delete is the one operation that's actually owner-only — anyone with the trip ID
        // can read or modify, but only the browser that created it can drop it.
        var trip = await trips.GetByIdAsync(id, ct).ConfigureAwait(false);
        if (trip is null || trip.OwnerId != session.SessionId)
        {
            return TypedResults.NotFound();
        }
        trips.Remove(trip);
        await trips.SaveChangesAsync(ct).ConfigureAwait(false);
        return TypedResults.NoContent();
    }
}
