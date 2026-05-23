namespace Trips.Core.Contracts;

/// <summary>Payload to POST /trips/{id}/lock-solution.</summary>
public sealed record LockSolutionRequest(Guid RunId, int ParetoIndex);

/// <summary>Payload to POST /trips/{id}/whatif. Warm-start re-optimise from the current locked solution.</summary>
public sealed record WhatIfRequest(
    IReadOnlyList<Guid>? DropParticipantIds,
    IReadOnlyList<AddParticipantRequest>? AddParticipants,
    ObjectiveWeightsDto? NewWeights);

/// <summary>Response wrapper for endpoints that enqueue an optimisation run.</summary>
public sealed record EnqueueRunResponse(Guid RunId);

/// <summary>
/// Cost-split breakdown for a locked solution. Itemises fuel + tolls per participant; the totals
/// at the top tier mirror what the driver paid, the per-entry shares sum to the same number.
/// </summary>
/// <param name="TripId">The owning trip.</param>
/// <param name="SolutionId">The locked solution we split.</param>
/// <param name="Entries">One entry per participating passenger.</param>
/// <param name="TotalCost">Sum of <see cref="TotalFuel"/> + <see cref="TotalTolls"/>; same number that the driver actually spent.</param>
/// <param name="TotalFuel">Total fuel cost for the trip.</param>
/// <param name="TotalTolls">Total toll cost for the trip; 0 when no tolls were provided.</param>
/// <param name="FuelPricePerLitre">Echo of the input fuel price (so the client can render units).</param>
/// <param name="FuelEconomyLPer100Km">Echo of the input fuel economy.</param>
public sealed record CostSplitResponse(
    Guid TripId,
    Guid? SolutionId,
    IReadOnlyList<CostSplitEntry> Entries,
    double TotalCost,
    double TotalFuel,
    double TotalTolls,
    double FuelPricePerLitre,
    double FuelEconomyLPer100Km);

/// <summary>One passenger's share of the trip cost.</summary>
/// <param name="ParticipantId">Domain id of the passenger.</param>
/// <param name="DisplayName">Passenger's display name for the UI.</param>
/// <param name="FuelShare">Their share of the fuel cost.</param>
/// <param name="TollShare">Their share of the tolls.</param>
/// <param name="Total">Sum of <see cref="FuelShare"/> + <see cref="TollShare"/>.</param>
public sealed record CostSplitEntry(
    Guid ParticipantId,
    string DisplayName,
    double FuelShare,
    double TollShare,
    double Total);

/// <summary>Optional toll segment supplied via query/body to the cost-split endpoint.</summary>
public sealed record TollSegmentDto(Guid FromStopId, Guid ToStopId, double Amount);

/// <summary>Payload to POST /trips/{id}/return-leg.</summary>
public sealed record ReturnLegRequest(IReadOnlyList<ReturnRequestDto> Requests);

/// <summary>One return-trip request.</summary>
public sealed record ReturnRequestDto(
    Guid ParticipantId,
    DateTime DesiredDeparture,
    double DropoffLongitude,
    double DropoffLatitude);

/// <summary>Response from POST /trips/{id}/return-leg — one solution per departure cluster.</summary>
public sealed record ReturnLegResponse(IReadOnlyList<SolutionDto> Solutions);

/// <summary>Optional query/body for the cost-split endpoint.</summary>
public sealed record CostSplitInputsDto(
    double? FuelPricePerLitre,
    double? FuelEconomyLPer100Km,
    IReadOnlyList<TollSegmentDto>? Tolls);
