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
    Guid? LockedSolutionId);

/// <summary>Payload to POST /trips.</summary>
public sealed record CreateTripRequest(
    string Name,
    string DestinationName,
    double DestinationLongitude,
    double DestinationLatitude,
    DateTimeOffset DepartAt,
    DateTimeOffset ArrivalWindowEarliest,
    DateTimeOffset ArrivalWindowLatest);
