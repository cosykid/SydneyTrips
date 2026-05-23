namespace Trips.Core.Contracts;

/// <summary>API view of a participant.</summary>
public sealed record ParticipantDto(
    Guid Id,
    Guid TripId,
    Guid UserId,
    string DisplayName,
    double HomeLongitude,
    double HomeLatitude,
    bool HasCar,
    int Seats,
    PreferencesDto Preferences);

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
