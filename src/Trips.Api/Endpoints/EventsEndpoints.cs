using Microsoft.AspNetCore.Http.HttpResults;
using Trips.Api.Auth;
using Trips.Core.Abstractions;
using Trips.Core.Contracts;

namespace Trips.Api.Endpoints;

public static class EventsEndpoints
{
    public static IEndpointRouteBuilder MapEvents(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var group = app.MapGroup("/trips/{tripId:guid}/events")
            .WithTags("Events")
            .RequireAuthorization();

        group.MapGet("/", ListAsync).WithName("ListTripEvents");

        return app;
    }

    private static async Task<Results<Ok<IReadOnlyList<TripEventDto>>, NotFound>> ListAsync(
        Guid tripId,
        DateTimeOffset? since,
        TripAuthorizationService authz,
        ITripEventRepository events,
        CurrentUser currentUser,
        CancellationToken ct)
    {
        var trip = await authz.AuthorizeAsync(tripId, currentUser.UserIdGuid, ct).ConfigureAwait(false);
        if (trip is null)
        {
            return TypedResults.NotFound();
        }

        var rows = since.HasValue
            ? await events.ListSinceAsync(tripId, since.Value, ct).ConfigureAwait(false)
            : await events.ListForTripAsync(tripId, ct).ConfigureAwait(false);

        IReadOnlyList<TripEventDto> dtos = rows.Select(e => new TripEventDto(
            Id: e.Id,
            TripId: e.TripId,
            Kind: e.Kind,
            ActorId: e.ActorId,
            Longitude: e.Location?.X,
            Latitude: e.Location?.Y,
            Timestamp: e.Timestamp,
            PayloadJson: e.PayloadJson)).ToList();

        return TypedResults.Ok(dtos);
    }
}
