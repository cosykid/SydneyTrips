using Trips.Core.Abstractions;
using Trips.Core.Domain;

namespace Trips.Optimisation.Tests.Helpers;

/// <summary>
/// Tiny builder for hand-crafted <see cref="SolverInput"/> instances used by unit tests. Lets the
/// tests stay short and declarative instead of pasting record literals everywhere.
/// </summary>
internal static class InstanceBuilder
{
    /// <summary>
    /// Build a <see cref="SolverInput"/> from a few primitive lists. Nodes are laid out as:
    /// <c>[ d0_origin, d1_origin, …, candidate_0, candidate_1, …, destination ]</c>.
    /// </summary>
    public static SolverInput Build(
        int driverCount,
        int[] driverSeats,
        int[][] passengerCandidatesLocal,  // per passenger: indices INTO the candidates array (not absolute node indices)
        int[][] passengerWalks,            // per passenger: walk-PT minutes per candidate
        int candidateCount,
        double[,] matrix,
        ObjectiveWeights? weights = null)
    {
        var drivers = new List<SolverDriver>(driverCount);
        for (var d = 0; d < driverCount; d++) drivers.Add(new SolverDriver(Guid.NewGuid(), d, driverSeats[d]));

        var nodes = new List<SolverNode>();
        for (var i = 0; i < driverCount; i++) nodes.Add(new SolverNode(i, NodeKind.Home, null));
        for (var i = 0; i < candidateCount; i++) nodes.Add(new SolverNode(driverCount + i, NodeKind.Home, Guid.NewGuid()));
        nodes.Add(new SolverNode(driverCount + candidateCount, NodeKind.TrainStation, null)); // destination

        var passengers = new List<SolverPassenger>(passengerCandidatesLocal.Length);
        for (var p = 0; p < passengerCandidatesLocal.Length; p++)
        {
            var absolute = passengerCandidatesLocal[p].Select(k => driverCount + k).ToArray();
            passengers.Add(new SolverPassenger(Guid.NewGuid(), absolute, passengerWalks[p]));
        }

        return new SolverInput(
            RunId: Guid.NewGuid(),
            TripId: Guid.NewGuid(),
            Weights: weights ?? ObjectiveWeights.Balanced,
            Drivers: drivers,
            Passengers: passengers,
            Nodes: nodes,
            TravelMatrix: matrix,
            DepartAt: DateTimeOffset.UtcNow);
    }

    /// <summary>
    /// Build a symmetric travel matrix from a flat upper-triangle list of (i, j, mins) tuples.
    /// Diagonal entries are 0. Unspecified entries default to a large finite value.
    /// </summary>
    public static double[,] SymmetricMatrix(int n, double defaultValue, params (int i, int j, double v)[] entries)
    {
        var m = new double[n, n];
        for (var i = 0; i < n; i++)
            for (var j = 0; j < n; j++)
                m[i, j] = i == j ? 0 : defaultValue;
        foreach (var (i, j, v) in entries)
        {
            m[i, j] = v; m[j, i] = v;
        }
        return m;
    }
}
