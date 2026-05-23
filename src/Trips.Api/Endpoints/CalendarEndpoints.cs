using System.Globalization;
using System.Text;
using Ical.Net.CalendarComponents;
using Ical.Net.DataTypes;
using Ical.Net.Serialization;
using Microsoft.AspNetCore.Http.HttpResults;
using Microsoft.EntityFrameworkCore;
using Trips.Api.Auth;
using Trips.Core.Abstractions;
using Trips.Core.Domain;
using Trips.Data;
using IcalCalendar = Ical.Net.Calendar;

namespace Trips.Api.Endpoints;

/// <summary>
/// ICS calendar generation. Produces a single VEVENT per participant containing their pickup
/// time + location. Reverse-geocodes the location to a human-readable address when
/// <see cref="IGeocodingClient"/> is available, otherwise falls back to lat/lng literal.
/// </summary>
public static class CalendarEndpoints
{
    public static IEndpointRouteBuilder MapCalendar(this IEndpointRouteBuilder app)
    {
        ArgumentNullException.ThrowIfNull(app);
        var group = app.MapGroup("/trips/{tripId:guid}/participants/{participantId:guid}")
            .WithTags("Calendar")
            .RequireAuthorization();

        group.MapGet("/calendar.ics", GetCalendarAsync).WithName("ParticipantCalendar");
        return app;
    }

    private static async Task<Results<ContentHttpResult, NotFound, ForbidHttpResult>> GetCalendarAsync(
        Guid tripId,
        Guid participantId,
        TripsDbContext db,
        TripAuthorizationService authz,
        IGeocodingClient? geocoding,
        CurrentUser currentUser,
        CancellationToken ct)
    {
        // Authorize: must be a trip participant or the trip owner.
        var trip = await authz.AuthorizeAsync(tripId, currentUser.UserIdGuid, ct).ConfigureAwait(false);
        if (trip is null)
        {
            return TypedResults.NotFound();
        }
        if (trip.LockedSolutionId is null)
        {
            return TypedResults.NotFound();
        }

        var participant = await db.Participants
            .FirstOrDefaultAsync(p => p.Id == participantId && p.TripId == tripId, ct).ConfigureAwait(false);
        if (participant is null)
        {
            return TypedResults.NotFound();
        }

        // Authorisation tightening: the caller is either the trip owner or the participant themselves.
        // Other participants of the trip do not get someone else's calendar — that would leak pickup
        // locations and contact graph beyond what the cost-split / lock endpoints expose.
        if (currentUser.UserIdGuid != trip.OwnerId && currentUser.UserIdGuid != participant.UserId)
        {
            return TypedResults.Forbid();
        }

        var solution = await db.Solutions
            .Include(s => s.Routes)
                .ThenInclude(r => r.Stops)
            .FirstOrDefaultAsync(s => s.Id == trip.LockedSolutionId, ct).ConfigureAwait(false);
        if (solution is null)
        {
            return TypedResults.NotFound();
        }

        // Find this participant's stop. They may be a driver (no pickup; their stop is the origin)
        // or a passenger (look across stops for their id in Pickups).
        Stop? myStop = null;
        DriverRoute? myRoute = null;
        foreach (var route in solution.Routes)
        {
            if (route.DriverId == participant.Id)
            {
                myRoute = route;
                myStop = route.Stops.OrderBy(s => s.OrderIndex).FirstOrDefault();
                break;
            }
            foreach (var stop in route.Stops)
            {
                if (stop.Pickups.Contains(participant.Id))
                {
                    myStop = stop;
                    myRoute = route;
                    break;
                }
            }
            if (myStop is not null) break;
        }

        if (myRoute is null)
        {
            return TypedResults.NotFound();
        }

        var pickupTime = myStop?.EstimatedArrival ?? trip.DepartAt;
        var locationDesc = myStop is not null
            ? await DescribeLocationAsync(myStop, geocoding, ct).ConfigureAwait(false)
            : await DescribeHomeAsync(participant, geocoding, ct).ConfigureAwait(false);

        var ics = BuildIcs(trip, participant, pickupTime, locationDesc);

        return TypedResults.Text(ics, "text/calendar", Encoding.UTF8);
    }

    private static async Task<string> DescribeLocationAsync(Stop stop, IGeocodingClient? geocoding, CancellationToken ct)
    {
        if (geocoding is not null)
        {
            try
            {
                var result = await geocoding.ReverseGeocodeAsync(stop.Location, ct).ConfigureAwait(false);
                if (result is not null && !string.IsNullOrWhiteSpace(result.FormattedAddress))
                {
                    return result.FormattedAddress;
                }
            }
            catch
            {
                // Fall through to the lat/lng literal — never fail the calendar download because
                // geocoding is degraded.
            }
        }
        return string.Format(CultureInfo.InvariantCulture, "{0:F5}, {1:F5}", stop.Location.Y, stop.Location.X);
    }

    private static async Task<string> DescribeHomeAsync(Participant participant, IGeocodingClient? geocoding, CancellationToken ct)
    {
        if (geocoding is not null)
        {
            try
            {
                var result = await geocoding.ReverseGeocodeAsync(participant.Home, ct).ConfigureAwait(false);
                if (result is not null && !string.IsNullOrWhiteSpace(result.FormattedAddress))
                {
                    return result.FormattedAddress;
                }
            }
            catch
            {
                // Same as above — never fail.
            }
        }
        return string.Format(CultureInfo.InvariantCulture, "{0:F5}, {1:F5}", participant.Home.Y, participant.Home.X);
    }

    /// <summary>
    /// Build the ICS payload via <c>Ical.Net</c>. We construct a single all-encompassing
    /// <see cref="CalendarEvent"/>: start = pickup time, duration = 15 minutes (a reasonable rendezvous
    /// window), description includes the trip name and destination. Calendar UID is deterministic per
    /// (trip, participant) so re-downloading the calendar updates the existing event in the user's app.
    /// </summary>
    private static string BuildIcs(Trip trip, Participant participant, DateTimeOffset pickupTime, string locationDesc)
    {
        var calendar = new IcalCalendar();
        calendar.AddProperty("PRODID", "-//SydneyTrips//WS7//EN");
        calendar.AddProperty("VERSION", "2.0");
        calendar.AddProperty("CALSCALE", "GREGORIAN");
        calendar.AddProperty("METHOD", "PUBLISH");

        var start = new CalDateTime(pickupTime.UtcDateTime, "UTC");
        var end = new CalDateTime(pickupTime.UtcDateTime.AddMinutes(15), "UTC");
        var evt = new CalendarEvent
        {
            Uid = $"trip-{trip.Id}-participant-{participant.Id}@sydneytrips",
            Summary = $"Pickup: {trip.Name}",
            Description = $"Trip to {trip.DestinationName}. Arrive at the pickup point by {pickupTime:HH:mm} on {pickupTime:yyyy-MM-dd}.",
            Location = locationDesc,
            Start = start,
            End = end,
        };
        calendar.Events.Add(evt);

        var serializer = new CalendarSerializer();
        return serializer.SerializeToString(calendar) ?? string.Empty;
    }
}
