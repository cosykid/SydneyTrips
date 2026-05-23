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
/// Cost-split breakdown for a locked solution. WS7 will fill in the math; until then we
/// return zeros plus a <see cref="Todo"/> note so the contract is stable for WS6.
/// </summary>
public sealed record CostSplitResponse(
    Guid TripId,
    Guid? SolutionId,
    IReadOnlyList<CostSplitEntry> Entries,
    double TotalCost,
    string? Todo);

public sealed record CostSplitEntry(
    Guid ParticipantId,
    string DisplayName,
    double Share,
    double Kilometres);
