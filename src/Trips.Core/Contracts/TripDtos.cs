namespace Trips.Core.Contracts;

/// <summary>API view of a trip — flattened so it can be serialised without exposing EF entities.</summary>
public sealed record TripDto(
    Guid Id,
    string Name,
    string DestinationName,
    double DestinationLongitude,
    double DestinationLatitude,
    DateTimeOffset DepartAt,
    DateTimeOffset ArrivalWindowEarliest,
    DateTimeOffset ArrivalWindowLatest,
    Guid OwnerId,
    DateTimeOffset CreatedAt,
    Guid? LockedSolutionId,
    int ParticipantCount);

/// <summary>Eager-loaded trip view used by GET /trips/{id}. Carries the participants
/// (with their candidate pickup nodes) inline so the planner / driver / cost views
/// can render in a single round-trip.</summary>
public sealed record TripDetailDto(
    Guid Id,
    string Name,
    string DestinationName,
    double DestinationLongitude,
    double DestinationLatitude,
    DateTimeOffset DepartAt,
    DateTimeOffset ArrivalWindowEarliest,
    DateTimeOffset ArrivalWindowLatest,
    Guid OwnerId,
    DateTimeOffset CreatedAt,
    Guid? LockedSolutionId,
    IReadOnlyList<ParticipantWithNodesDto> Participants);

/// <summary>Payload to POST /trips.</summary>
public sealed record CreateTripRequest(
    string Name,
    string DestinationName,
    double DestinationLongitude,
    double DestinationLatitude,
    DateTimeOffset DepartAt,
    DateTimeOffset ArrivalWindowEarliest,
    DateTimeOffset ArrivalWindowLatest);

/// <summary>Payload to PATCH /trips/{id}/destination.</summary>
public sealed record UpdateTripDestinationRequest(
    string DestinationName,
    double DestinationLongitude,
    double DestinationLatitude);
