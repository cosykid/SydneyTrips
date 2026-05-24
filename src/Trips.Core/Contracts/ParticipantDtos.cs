namespace Trips.Core.Contracts;

/// <summary>API view of a participant.</summary>
public sealed record ParticipantDto(
    Guid Id,
    Guid TripId,
    string DisplayName,
    double HomeLongitude,
    double HomeLatitude,
    bool HasCar,
    int Seats,
    PreferencesDto Preferences);

/// <summary>Participant with their candidate pickup nodes pre-loaded. Used inside
/// <see cref="TripDetailDto"/> so the planner UI does not have to fan-out a request
/// per participant.</summary>
public sealed record ParticipantWithNodesDto(
    Guid Id,
    Guid TripId,
    string DisplayName,
    double HomeLongitude,
    double HomeLatitude,
    bool HasCar,
    int Seats,
    PreferencesDto Preferences,
    IReadOnlyList<CandidateNodeDto> CandidateNodes);

/// <summary>API view of a <see cref="Trips.Core.Domain.CandidateNode"/>. The solver picks
/// one of these per assigned participant; the planner UI renders them as faint markers
/// so users can see the pickup options around each origin.
///
/// <para><c>Path</c> is the real PT geometry from the participant's home to this hub — when
/// non-null, the FE map draws this polyline instead of a straight crow-fly line. Null for
/// the Home node and for any node generated before the polyline feature shipped.</para></summary>
public sealed record CandidateNodeDto(
    Guid Id,
    Guid ParticipantId,
    CandidateNodeKindDto Kind,
    double Longitude,
    double Latitude,
    int WalkMins,
    int PtMins,
    string? ExternalId,
    string? DisplayName,
    PathDto? Path);

/// <summary>A polyline as an ordered <c>[longitude, latitude]</c> sequence. Kept flat (no
/// nested per-leg objects) for the simplest possible FE rendering — the FE just draws a
/// single line. Multi-leg colour-coding can come later by switching to a richer shape.</summary>
public sealed record PathDto(IReadOnlyList<PathCoordinateDto> Coordinates);

public sealed record PathCoordinateDto(double Longitude, double Latitude);

/// <summary>Wire-stable name for <see cref="Trips.Core.Domain.NodeKind"/>. We expose
/// strings rather than ints so the frontend can switch on values without depending on
/// the enum ordinal.</summary>
[System.Text.Json.Serialization.JsonConverter(typeof(Trips.Core.Serialization.CamelCaseEnumConverter))]
public enum CandidateNodeKindDto
{
    Home = 0,
    TrainStation = 1,
    BusStop = 2,
    Wharf = 3,
    LightRailStop = 4,
}

/// <summary>API representation of <see cref="Trips.Core.Domain.Preferences"/>.</summary>
public sealed record PreferencesDto(
    int WalkBudgetMins,
    int DetourToleranceMins,
    double FairnessWeight);

/// <summary>Payload to POST /trips/{id}/participants. The server auto-geocodes <c>HomeAddress</c> if coords are absent.</summary>
public sealed record AddParticipantRequest(
    string DisplayName,
    string? HomeAddress,
    double? HomeLongitude,
    double? HomeLatitude,
    bool HasCar,
    int Seats,
    PreferencesDto? Preferences);
