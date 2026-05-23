using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Trips.Core.Abstractions;
using Trips.Core.Domain;

namespace Trips.Optimisation.WhatIf;

/// <summary>
/// Re-optimises from a locked <see cref="Solution"/> with a delta (drop/add passengers, change
/// objective weights). Uses an OR-Tools warm-start hint to keep stops stable where possible — small
/// deltas produce *minimal-disruption* re-optimisation rather than a totally different plan.
/// </summary>
public interface IWhatIfService
{
    Task<Solution> ReoptimiseAsync(Guid lockedSolutionId, WhatIfDelta delta, CancellationToken ct);
}

/// <summary>Diff applied to a locked solution before re-optimising.</summary>
/// <param name="DropParticipantIds">Passengers to remove. Null/empty ⇒ keep everyone.</param>
/// <param name="AddParticipants">New passengers to add, with their candidate nodes. Null/empty ⇒ no additions.</param>
/// <param name="NewWeights">Replacement objective weights. Null ⇒ keep the original run's weights.</param>
public sealed record WhatIfDelta(
    IReadOnlyList<Guid>? DropParticipantIds,
    IReadOnlyList<AddParticipantDelta>? AddParticipants,
    ObjectiveWeights? NewWeights);

/// <summary>One added passenger in a <see cref="WhatIfDelta"/>.</summary>
/// <param name="ParticipantId">Domain id of the new participant.</param>
/// <param name="CandidateNodes">Their feasible pickup nodes. Must include at least one node that is
/// already present in the original <see cref="SolverInput.Nodes"/> table (the service stitches the
/// indices together by <see cref="SolverNode.CandidateNodeId"/>).</param>
public sealed record AddParticipantDelta(Guid ParticipantId, IReadOnlyList<SolverNode> CandidateNodes);

public sealed class WhatIfService : IWhatIfService
{
    private readonly ILockedContextRepository _contexts;
    private readonly ISolver _solver;
    private readonly ILogger<WhatIfService> _logger;

    public WhatIfService(
        ILockedContextRepository contexts,
        ISolver solver,
        ILogger<WhatIfService>? logger = null)
    {
        ArgumentNullException.ThrowIfNull(contexts);
        ArgumentNullException.ThrowIfNull(solver);
        _contexts = contexts;
        _solver = solver;
        _logger = logger ?? NullLogger<WhatIfService>.Instance;
    }

    public async Task<Solution> ReoptimiseAsync(Guid lockedSolutionId, WhatIfDelta delta, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(delta);
        var ctx = await _contexts.GetByIdAsync(lockedSolutionId, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException($"Locked solution {lockedSolutionId} not found.");

        var newInput = ApplyDelta(ctx, delta);
        _logger.LogInformation("WhatIfService: re-optimising from solution {Sol} with {Drop} drops, {Add} adds, weightsChanged={W}",
            lockedSolutionId,
            delta.DropParticipantIds?.Count ?? 0,
            delta.AddParticipants?.Count ?? 0,
            delta.NewWeights is not null);

        return await _solver.SolveAsync(newInput, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// Apply a delta to a (solution, input) pair: drop passengers, append new ones, optionally swap
    /// weights, and build a <see cref="Trips.Core.Abstractions.WarmStartHint"/> from the surviving
    /// assignments. Public so tests can exercise the transform without a repository.
    /// </summary>
    public static SolverInput ApplyDelta(LockedContext ctx, WhatIfDelta delta)
    {
        ArgumentNullException.ThrowIfNull(ctx);
        ArgumentNullException.ThrowIfNull(delta);

        var drops = new HashSet<Guid>(delta.DropParticipantIds ?? Array.Empty<Guid>());
        var originalInput = ctx.Input;

        // Filter out dropped passengers. We rebuild the passenger list and remember the index map so
        // we can translate the locked assignment indices into the new coordinate system.
        var keptPassengers = new List<SolverPassenger>(originalInput.Passengers.Count);
        var oldToNewPassengerIndex = new int[originalInput.Passengers.Count];
        for (var i = 0; i < originalInput.Passengers.Count; i++)
        {
            var p = originalInput.Passengers[i];
            if (drops.Contains(p.ParticipantId))
            {
                oldToNewPassengerIndex[i] = -1;
                continue;
            }
            oldToNewPassengerIndex[i] = keptPassengers.Count;
            keptPassengers.Add(p);
        }

        // Add new passengers. We assume their CandidateNodes are already present in the original
        // Nodes table (matched by CandidateNodeId). If a new candidate node id is unknown, append a
        // new node row and extend the matrix with placeholder times so the solver at least sees it.
        var nodes = originalInput.Nodes.ToList();
        var matrix = ExpandMatrixIfNeeded(originalInput.TravelMatrix, nodes.Count);
        var newPassengerIds = new List<Guid>();
        if (delta.AddParticipants is not null)
        {
            foreach (var add in delta.AddParticipants)
            {
                newPassengerIds.Add(add.ParticipantId);
                var candidateAbsoluteIndices = new List<int>(add.CandidateNodes.Count);
                var walks = new List<int>(add.CandidateNodes.Count);
                foreach (var cand in add.CandidateNodes)
                {
                    var existing = FindNodeIndex(nodes, cand);
                    if (existing >= 0)
                    {
                        candidateAbsoluteIndices.Add(existing);
                        walks.Add(0); // unknown walk; let the solver figure it out
                    }
                    else
                    {
                        // Append a new row + grow the matrix conservatively (use the max existing
                        // travel time as a pessimistic estimate so the new node looks "average" until
                        // a future matrix recompute fills in the real numbers).
                        nodes.Add(cand with { Index = nodes.Count });
                        matrix = GrowMatrix(matrix);
                        candidateAbsoluteIndices.Add(nodes.Count - 1);
                        walks.Add(0);
                    }
                }
                keptPassengers.Add(new SolverPassenger(
                    add.ParticipantId, candidateAbsoluteIndices, walks));
            }
        }

        // Build the warm-start hint from the surviving locked assignments. Iterate the locked
        // solution's stops; for each passenger pickup that's still kept, record (driver, passenger, node).
        var assignmentHints = new List<WarmStartAssignment>();
        var driverIndexByParticipant = new Dictionary<Guid, int>(originalInput.Drivers.Count);
        for (var d = 0; d < originalInput.Drivers.Count; d++)
        {
            driverIndexByParticipant[originalInput.Drivers[d].ParticipantId] = d;
        }
        var passengerIndexByParticipant = new Dictionary<Guid, int>(keptPassengers.Count);
        for (var p = 0; p < keptPassengers.Count; p++)
        {
            passengerIndexByParticipant[keptPassengers[p].ParticipantId] = p;
        }
        var nodeIndexByCandidate = new Dictionary<Guid, int>(nodes.Count);
        for (var i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].CandidateNodeId is { } cid && cid != Guid.Empty)
            {
                nodeIndexByCandidate[cid] = i;
            }
        }

        var driverSequences = new List<List<int>>(originalInput.Drivers.Count);
        for (var d = 0; d < originalInput.Drivers.Count; d++) driverSequences.Add(new List<int>());

        foreach (var route in ctx.Solution.Routes)
        {
            if (!driverIndexByParticipant.TryGetValue(route.DriverId, out var dIdx)) continue;
            foreach (var stop in route.Stops.OrderBy(s => s.OrderIndex))
            {
                if (!nodeIndexByCandidate.TryGetValue(stop.CandidateNodeId, out var nIdx)) continue;
                var anyKeptPickupHere = false;
                foreach (var pickup in stop.Pickups)
                {
                    if (drops.Contains(pickup)) continue;
                    if (!passengerIndexByParticipant.TryGetValue(pickup, out var pIdx)) continue;
                    assignmentHints.Add(new WarmStartAssignment(pIdx, dIdx, nIdx));
                    anyKeptPickupHere = true;
                }
                if (anyKeptPickupHere)
                {
                    driverSequences[dIdx].Add(nIdx);
                }
            }
        }

        var hint = new WarmStartHint(
            assignmentHints,
            driverSequences.Select(s => (IReadOnlyList<int>)s).ToList());

        return new SolverInput(
            RunId: Guid.NewGuid(),
            TripId: originalInput.TripId,
            Weights: delta.NewWeights ?? originalInput.Weights,
            Drivers: originalInput.Drivers,
            Passengers: keptPassengers,
            Nodes: nodes,
            TravelMatrix: matrix,
            DepartAt: originalInput.DepartAt,
            WarmStartHint: hint);
    }

    private static int FindNodeIndex(IReadOnlyList<SolverNode> nodes, SolverNode candidate)
    {
        if (candidate.CandidateNodeId is null) return -1;
        for (var i = 0; i < nodes.Count; i++)
        {
            if (nodes[i].CandidateNodeId == candidate.CandidateNodeId)
            {
                return i;
            }
        }
        return -1;
    }

    private static double[,] ExpandMatrixIfNeeded(double[,] matrix, int currentSize)
    {
        // No-op if dimensions already match — the caller hasn't appended any nodes yet.
        if (matrix.GetLength(0) >= currentSize && matrix.GetLength(1) >= currentSize) return matrix;
        return GrowMatrix(matrix);
    }

    private static double[,] GrowMatrix(double[,] matrix)
    {
        var oldSize = matrix.GetLength(0);
        var newSize = oldSize + 1;
        var bigger = new double[newSize, newSize];
        var avg = 0.0;
        var count = 0;
        for (var i = 0; i < oldSize; i++)
        {
            for (var j = 0; j < oldSize; j++)
            {
                bigger[i, j] = matrix[i, j];
                if (i != j) { avg += matrix[i, j]; count++; }
            }
        }
        var fill = count == 0 ? 10.0 : avg / count;
        for (var i = 0; i < newSize; i++)
        {
            if (i == newSize - 1) continue;
            bigger[i, newSize - 1] = fill;
            bigger[newSize - 1, i] = fill;
        }
        bigger[newSize - 1, newSize - 1] = 0.0;
        return bigger;
    }
}
