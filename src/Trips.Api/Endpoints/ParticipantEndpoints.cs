using Microsoft.AspNetCore.Http.HttpResults;
using NetTopologySuite.Geometries;
using Trips.Api.Auth;
using Trips.Api.Mapping;
using Trips.Api.Services;
using Trips.Api.Validation;
using Trips.Core.Abstractions;
using Trips.Core.Contracts;
using Trips.Core.Domain;

namespace Trips.Api.Endpoints;

public static class ParticipantEndpoints
{
    public static IEndpointRouteBuilder MapParticipants(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var group = app.MapGroup("/trips/{tripId:guid}/participants")
            .WithTags("Participants");

        group.MapPost("/", AddAsync)
            .AddEndpointFilter<ValidationFilter<AddParticipantRequest>>()
            .WithName("AddParticipant");

        group.MapPatch("/{pid:guid}/prefs", UpdatePrefsAsync)
            .AddEndpointFilter<ValidationFilter<PreferencesDto>>()
            .WithName("UpdateParticipantPrefs");

        group.MapDelete("/{pid:guid}", RemoveAsync).WithName("RemoveParticipant");

        return app;
    }

    private static async Task<Results<Created<ParticipantDto>, NotFound, ProblemHttpResult>> AddAsync(
        Guid tripId,
        AddParticipantRequest request,
        TripAuthorizationService authz,
        IParticipantRepository participants,
        ITripEventRepository events,
        ParticipantCandidateNodeService candidateNodes,
        IGeocodingClient geocoding,
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

        var factory = new GeometryFactory(new PrecisionModel(), 4326);
        Point home;
        if (request.HomeLongitude.HasValue && request.HomeLatitude.HasValue)
        {
            home = factory.CreatePoint(new Coordinate(request.HomeLongitude.Value, request.HomeLatitude.Value));
        }
        else if (!string.IsNullOrWhiteSpace(request.HomeAddress))
        {
            GeocodingResult? geo;
            try
            {
                geo = await geocoding.GeocodeAsync(request.HomeAddress, ct).ConfigureAwait(false);
            }
            catch (NotImplementedException)
            {
                return TypedResults.Problem(
                    detail: "Geocoding is not available in this environment; supply HomeLongitude/HomeLatitude explicitly.",
                    statusCode: StatusCodes.Status503ServiceUnavailable,
                    title: "Geocoding unavailable",
                    extensions: Trace(http));
            }
            if (geo is null)
            {
                return TypedResults.Problem(
                    detail: "Could not geocode the supplied address.",
                    statusCode: StatusCodes.Status400BadRequest,
                    title: "Invalid address",
                    extensions: Trace(http));
            }
            home = geo.Location;
        }
        else
        {
            return TypedResults.Problem(
                detail: "Either HomeAddress or HomeLongitude/HomeLatitude must be provided.",
                statusCode: StatusCodes.Status400BadRequest,
                title: "Validation failed",
                extensions: Trace(http));
        }

        var preferences = request.Preferences is null
            ? Preferences.Default
            : new Preferences(request.Preferences.WalkBudgetMins, request.Preferences.DetourToleranceMins, request.Preferences.FairnessWeight);

        var participant = new Participant(
            id: Guid.NewGuid(),
            tripId: tripId,
            displayName: request.DisplayName,
            home: home,
            hasCar: request.HasCar,
            seats: request.Seats,
            preferences: preferences);

        await candidateNodes.PopulateAsync(participant, ct).ConfigureAwait(false);

        await participants.AddAsync(participant, ct).ConfigureAwait(false);
        await events.AddAsync(new TripEvent(
            id: Guid.NewGuid(),
            tripId: tripId,
            kind: EventKind.ParticipantAdded,
            actorId: session.SessionId,
            location: home,
            timestamp: clock.UtcNow), ct).ConfigureAwait(false);
        await participants.SaveChangesAsync(ct).ConfigureAwait(false);

        return TypedResults.Created($"/trips/{tripId}/participants/{participant.Id}", participant.ToDto());
    }

    private static async Task<Results<Ok<ParticipantDto>, NotFound>> UpdatePrefsAsync(
        Guid tripId,
        Guid pid,
        PreferencesDto prefs,
        TripAuthorizationService authz,
        IParticipantRepository participants,
        CancellationToken ct)
    {
        var trip = await authz.LookupAsync(tripId, ct).ConfigureAwait(false);
        if (trip is null)
        {
            return TypedResults.NotFound();
        }

        var participant = await participants.GetByIdAsync(pid, ct).ConfigureAwait(false);
        if (participant is null || participant.TripId != tripId)
        {
            return TypedResults.NotFound();
        }

        participant.UpdatePreferences(new Preferences(prefs.WalkBudgetMins, prefs.DetourToleranceMins, prefs.FairnessWeight));
        await participants.SaveChangesAsync(ct).ConfigureAwait(false);
        return TypedResults.Ok(participant.ToDto());
    }

    private static async Task<Results<NoContent, NotFound>> RemoveAsync(
        Guid tripId,
        Guid pid,
        TripAuthorizationService authz,
        IParticipantRepository participants,
        ITripEventRepository events,
        CurrentSession session,
        IClock clock,
        CancellationToken ct)
    {
        var trip = await authz.LookupAsync(tripId, ct).ConfigureAwait(false);
        if (trip is null)
        {
            return TypedResults.NotFound();
        }

        var participant = await participants.GetByIdAsync(pid, ct).ConfigureAwait(false);
        if (participant is null || participant.TripId != tripId)
        {
            return TypedResults.NotFound();
        }

        participants.Remove(participant);
        await events.AddAsync(new TripEvent(
            id: Guid.NewGuid(),
            tripId: tripId,
            kind: EventKind.ParticipantRemoved,
            actorId: session.SessionId,
            location: participant.Home,
            timestamp: clock.UtcNow), ct).ConfigureAwait(false);
        await participants.SaveChangesAsync(ct).ConfigureAwait(false);
        return TypedResults.NoContent();
    }

    private static IDictionary<string, object?> Trace(HttpContext http) =>
        new Dictionary<string, object?> { ["traceId"] = http.TraceIdentifier };
}
