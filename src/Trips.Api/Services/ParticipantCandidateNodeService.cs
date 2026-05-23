using NetTopologySuite.Geometries;
using Trips.Core.Abstractions;
using Trips.Core.Domain;

namespace Trips.Api.Services;

/// <summary>
/// Builds the candidate-node set for a freshly-added participant.
/// Adds the Home node and queries <see cref="ITfNswClient.CoordinateRequestAsync"/> for nearby
/// PT stops within the participant's walk budget (used as a rough metres radius via 80 m/min).
/// </summary>
public sealed class ParticipantCandidateNodeService
{
    private const int MetresPerWalkMinute = 80;

    private readonly ITfNswClient _tfnsw;
    private readonly ILogger<ParticipantCandidateNodeService> _logger;

    public ParticipantCandidateNodeService(ITfNswClient tfnsw, ILogger<ParticipantCandidateNodeService> logger)
    {
        ArgumentNullException.ThrowIfNull(tfnsw);
        ArgumentNullException.ThrowIfNull(logger);
        _tfnsw = tfnsw;
        _logger = logger;
    }

    public async Task PopulateAsync(Participant participant, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(participant);

        // Always include Home as a candidate.
        participant.AddCandidateNode(new CandidateNode(
            id: Guid.NewGuid(),
            participantId: participant.Id,
            kind: NodeKind.Home,
            location: participant.Home,
            walkMins: 0,
            ptMins: 0,
            displayName: "Home"));

        var radius = Math.Max(200, participant.WalkBudgetMins * MetresPerWalkMinute);
        try
        {
            var stops = await _tfnsw.CoordinateRequestAsync(participant.Home, radius, ct).ConfigureAwait(false);
            foreach (var stop in stops)
            {
                var walkMins = Math.Max(1, stop.DistanceMeters / MetresPerWalkMinute);
                if (walkMins > participant.WalkBudgetMins)
                {
                    continue;
                }
                var kind = ClassifyStopKind(stop.Mode);
                participant.AddCandidateNode(new CandidateNode(
                    id: Guid.NewGuid(),
                    participantId: participant.Id,
                    kind: kind,
                    location: stop.Location,
                    walkMins: walkMins,
                    ptMins: 0,
                    externalId: stop.StopId,
                    displayName: stop.Name));
            }
        }
        catch (NotImplementedException)
        {
            _logger.LogInformation("TfNSW client not wired; participant {Participant} has Home-only candidate nodes", participant.Id);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "Failed to fetch nearby stops for participant {Participant}; falling back to Home-only", participant.Id);
        }
    }

    private static NodeKind ClassifyStopKind(string mode)
    {
        return (mode ?? string.Empty).ToLowerInvariant() switch
        {
            "train" or "rail" or "heavy_rail" or "metro" => NodeKind.TrainStation,
            "light_rail" or "tram" or "lightrail" => NodeKind.LightRailStop,
            "ferry" or "wharf" => NodeKind.Wharf,
            _ => NodeKind.BusStop,
        };
    }

    public static double DistanceMetres(Point a, Point b)
    {
        ArgumentNullException.ThrowIfNull(a);
        ArgumentNullException.ThrowIfNull(b);
        return a.Distance(b) * 111_000.0;
    }
}
