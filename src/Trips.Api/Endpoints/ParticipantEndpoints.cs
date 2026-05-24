using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using NetTopologySuite.Geometries;
using Trips.Api.Auth;
using Trips.Api.Mapping;
using Trips.Api.Services;
using Trips.Api.Validation;
using Trips.Core.Abstractions;
using Trips.Core.Contracts;
using Trips.Core.Domain;
using Trips.Data;

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

        // Trip-level refresh: re-runs ParticipantCandidateNodeService.PopulateAsync against every
        // participant so stale candidate-node sets (e.g. generated against an older TfNSW client
        // or stub) get rebuilt. Same auth as the rest of the participants group.
        app.MapPost("/trips/{tripId:guid}/refresh-candidate-nodes", RefreshCandidateNodesAsync)
            .WithTags("Participants")
            .WithName("RefreshCandidateNodes");

        return app;
    }

    private static async Task<Results<Ok<RefreshCandidateNodesResponse>, NotFound>> RefreshCandidateNodesAsync(
        Guid tripId,
        TripAuthorizationService authz,
        TripsDbContext db,
        ParticipantCandidateNodeService candidateNodes,
        CancellationToken ct)
    {
        var lookup = await authz.LookupAsync(tripId, ct).ConfigureAwait(false);
        if (lookup is null)
        {
            return TypedResults.NotFound();
        }

        // Load trip + participants + their candidate nodes inside this scope's DbContext so
        // change-tracking sees the removals when we delete them below.
        var trip = await db.Trips
            .Include(t => t.Participants)
                .ThenInclude(p => p.CandidateNodes)
            .FirstOrDefaultAsync(t => t.Id == tripId, ct)
            .ConfigureAwait(false);
        if (trip is null)
        {
            return TypedResults.NotFound();
        }

        var totalBefore = trip.Participants.Sum(p => p.CandidateNodes.Count);

        // Two-phase: (1) delete the existing candidate-node rows via the DbContext, save so the
        // delete commits before we try to insert fresh ones with new GUIDs. (2) clear the in-memory
        // collection, populate, save. Doing it in one SaveChanges call hits an EF tracking edge
        // case where the cleared nav-collection items aren't marked Deleted automatically (probably
        // because CandidateNode is a private-list-backed nav with a non-nullable FK).
        // Bulk-delete via raw SQL so we don't have to track each row through the change tracker —
        // sidesteps an EF cascade/orphan edge case where ClearCandidateNodes() doesn't reliably
        // mark co-tracked rows as Deleted.
        var participantIds = trip.Participants.Select(p => p.Id).ToList();
        await db.Set<CandidateNode>()
            .Where(cn => participantIds.Contains(cn.ParticipantId))
            .ExecuteDeleteAsync(ct)
            .ConfigureAwait(false);

        // ExecuteDeleteAsync bypasses the change tracker, so anything we loaded before that call
        // still has the now-gone CandidateNode rows pinned as Unchanged. Start a clean slate.
        db.ChangeTracker.Clear();

        // Re-load each participant fresh and populate. Saving per-participant keeps the
        // change-tracker scope small and surfaces any TfNSW failure against the specific row
        // rather than as a blast-radius rollback at the end.
        // Re-populate per-participant in fresh scopes (ChangeTracker.Clear between iterations) so
        // EF tracking stays predictable across the loop. Each new CandidateNode is marked Added
        // explicitly — relying on relationship-fixup via the private-list nav property is
        // unreliable for participants loaded as Unchanged (the change-detector doesn't always
        // see new items appended after load).
        var totalAfter = 0;
        foreach (var pid in participantIds)
        {
            var participant = await db.Set<Participant>()
                .Include(p => p.CandidateNodes)
                .FirstOrDefaultAsync(p => p.Id == pid, ct)
                .ConfigureAwait(false);
            if (participant is null) continue;
            await candidateNodes.PopulateAsync(participant, trip, ct).ConfigureAwait(false);
            foreach (var cn in participant.CandidateNodes)
            {
                var entry = db.Entry(cn);
                if (entry.State == EntityState.Detached)
                {
                    entry.State = EntityState.Added;
                }
            }
            await db.SaveChangesAsync(ct).ConfigureAwait(false);
            totalAfter += participant.CandidateNodes.Count;
            db.ChangeTracker.Clear();
        }

        return TypedResults.Ok(new RefreshCandidateNodesResponse(
            ParticipantsRefreshed: participantIds.Count,
            CandidateNodesBefore: totalBefore,
            CandidateNodesAfter: totalAfter));
    }

    private static async Task<Results<Created<ParticipantDto>, NotFound, ProblemHttpResult>> AddAsync(
        Guid tripId,
        AddParticipantRequest request,
        ITripRepository trips,
        IParticipantRepository participants,
        ITripEventRepository events,
        ParticipantCandidateNodeService candidateNodes,
        IGeocodingClient geocoding,
        CurrentSession session,
        IClock clock,
        HttpContext http,
        CancellationToken ct)
    {
        // Load the trip *with* its existing participants — the candidate-node service inspects
        // `trip.Participants.Where(p => p.HasCar)` to find driver corridors, and a bare
        // `GetByIdAsync` returns a trip whose `Participants` collection is empty.
        var trip = await trips.GetWithParticipantsAsync(tripId, ct).ConfigureAwait(false);
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

        await candidateNodes.PopulateAsync(participant, trip, ct).ConfigureAwait(false);

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

/// <summary>Response from POST /trips/{id}/refresh-candidate-nodes — counts before/after so the
/// caller can verify the refresh actually changed something (e.g. stub-era trips going from
/// 1 candidate per participant to many).</summary>
public sealed record RefreshCandidateNodesResponse(
    int ParticipantsRefreshed,
    int CandidateNodesBefore,
    int CandidateNodesAfter);
