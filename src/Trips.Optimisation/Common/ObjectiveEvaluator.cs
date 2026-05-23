using Trips.Core.Abstractions;
using Trips.Core.Domain;

namespace Trips.Optimisation.Common;

/// <summary>
/// Centralised, deterministic evaluation of the multi-objective cost used by both
/// <see cref="OrTools.OrToolsSolver"/> and <see cref="Heuristic.HeuristicSolver"/>. Keeping a single
/// implementation guarantees that the two solvers' objective values are directly comparable in the
/// benchmark report — they're not just both calling something called "Evaluate", they're literally
/// calling the same function.
/// </summary>
public static class ObjectiveEvaluator
{
    /// <summary>
    /// Fixed cost added per stop visited. Mirrors the <c>β · Σ visit[d,n]</c> term in the
    /// formulation: the multiplier α/β etc. comes from the weights, this is the per-unit value.
    /// </summary>
    public const double StopCost = 1.0;

    /// <summary>
    /// Big-M used by OR-Tools for arrival propagation. Centralised so the heuristic and CP-SAT models
    /// stay in lockstep.
    /// </summary>
    public const int BigMArrivalMinutes = 24 * 60;

    /// <summary>
    /// Evaluate a candidate assignment + ordering. Returns the scalar objective and the per-term
    /// breakdown that gets written into <see cref="Solution.ObjectiveTerms"/>.
    /// </summary>
    /// <param name="input">Solver input describing the instance.</param>
    /// <param name="routesPerDriver">For each driver index in <see cref="SolverInput.Drivers"/>, the
    /// ordered list of node indices the driver visits (excluding origin, excluding destination).</param>
    /// <param name="nodeChoicePerPassenger">For each passenger index in <see cref="SolverInput.Passengers"/>,
    /// the chosen node index (must be one of the passenger's candidate nodes).</param>
    /// <param name="driverPerPassenger">For each passenger index, the assigned driver index.</param>
    /// <param name="destinationNodeIndex">Index of the destination node in the matrix.</param>
    public static EvaluationResult Evaluate(
        SolverInput input,
        IReadOnlyList<IReadOnlyList<int>> routesPerDriver,
        IReadOnlyList<int> nodeChoicePerPassenger,
        IReadOnlyList<int> driverPerPassenger,
        int destinationNodeIndex)
    {
        ArgumentNullException.ThrowIfNull(input);
        ArgumentNullException.ThrowIfNull(routesPerDriver);

        var weights = input.Weights;
        var matrix = input.TravelMatrix;

        // α · Σ travel_d — idle drivers (those carrying no passenger) stay home and contribute zero
        // travel; this matches the CP-SAT formulation where origin out-degree = hasLoad.
        var driverHasLoad = new bool[input.Drivers.Count];
        for (var p = 0; p < input.Passengers.Count; p++)
        {
            driverHasLoad[driverPerPassenger[p]] = true;
        }
        double travel = 0.0;
        var driverTravel = new double[input.Drivers.Count];
        for (var d = 0; d < input.Drivers.Count; d++)
        {
            if (!driverHasLoad[d])
            {
                driverTravel[d] = 0.0;
                continue;
            }
            var driver = input.Drivers[d];
            var seq = routesPerDriver[d];
            var prev = driver.OriginNodeIndex;
            double t = 0.0;
            foreach (var n in seq)
            {
                t += matrix[prev, n];
                prev = n;
            }
            t += matrix[prev, destinationNodeIndex];
            driverTravel[d] = t;
            travel += t;
        }

        // β · Σ visit[d,n]  — count stops actually visited (not destination, not origin)
        double stops = 0.0;
        for (var d = 0; d < routesPerDriver.Count; d++)
        {
            stops += routesPerDriver[d].Count * StopCost;
        }

        // γ · Σ (walk+pt)(p,n)·assign[d,p,n]
        double walk = 0.0;
        var passengerArrival = new double[input.Passengers.Count];
        for (var p = 0; p < input.Passengers.Count; p++)
        {
            var passenger = input.Passengers[p];
            var chosenNode = nodeChoicePerPassenger[p];
            var localIndex = IndexOf(passenger.CandidateNodeIndices, chosenNode);
            if (localIndex < 0)
            {
                // Infeasible — return very high cost so any caller comparing solutions discards it.
                return EvaluationResult.Infeasible;
            }
            walk += passenger.WalkPtMinsByNodeIndex[localIndex];

            // For arrival-spread / fairness we record the *driver*'s arrival at destination — every
            // passenger on the same driver arrives together. Compute below.
            passengerArrival[p] = double.NaN;
        }

        // Compute each driver's arrival time at the destination so we can stamp it onto passengers.
        var driverArrival = new double[input.Drivers.Count];
        for (var d = 0; d < input.Drivers.Count; d++)
        {
            driverArrival[d] = driverTravel[d];
        }
        for (var p = 0; p < input.Passengers.Count; p++)
        {
            passengerArrival[p] = driverArrival[driverPerPassenger[p]];
        }

        // δ · spread(arrivals) — max minus min across passengers
        double spread = 0.0;
        if (passengerArrival.Length > 0)
        {
            double min = double.PositiveInfinity, max = double.NegativeInfinity;
            for (var i = 0; i < passengerArrival.Length; i++)
            {
                if (passengerArrival[i] < min) min = passengerArrival[i];
                if (passengerArrival[i] > max) max = passengerArrival[i];
            }
            spread = max - min;
        }

        // ε · fairness — max-individual minus min-individual journey cost (walk + drive). This is the
        // worst-passenger-minus-best-passenger gap; the formulation calls it the "fairness penalty"
        // and the CP-SAT model encodes the identical surrogate so the two objective values stay
        // directly comparable in the benchmark report.
        double fairness = 0.0;
        if (input.Passengers.Count > 0)
        {
            double minJ = double.PositiveInfinity, maxJ = double.NegativeInfinity;
            for (var p = 0; p < input.Passengers.Count; p++)
            {
                var passenger = input.Passengers[p];
                var localIndex = IndexOf(passenger.CandidateNodeIndices, nodeChoicePerPassenger[p]);
                var journey = passenger.WalkPtMinsByNodeIndex[localIndex] + driverArrival[driverPerPassenger[p]];
                if (journey < minJ) minJ = journey;
                if (journey > maxJ) maxJ = journey;
            }
            fairness = maxJ - minJ;
        }

        var terms = new[]
        {
            weights.DriveTime * travel,
            weights.StopCount * stops,
            weights.WalkAndPt * walk,
            weights.ArrivalSpread * spread,
            weights.Fairness * fairness,
        };
        var total = terms[0] + terms[1] + terms[2] + terms[3] + terms[4];

        return new EvaluationResult(total, terms, driverTravel, driverArrival);
    }

    private static int IndexOf(IReadOnlyList<int> list, int value)
    {
        for (var i = 0; i < list.Count; i++)
        {
            if (list[i] == value) return i;
        }
        return -1;
    }
}

/// <summary>Result of <see cref="ObjectiveEvaluator.Evaluate"/>.</summary>
public sealed record EvaluationResult(
    double Objective,
    double[] Terms,
    double[] DriverTravelMins,
    double[] DriverArrivalMins)
{
    public static EvaluationResult Infeasible { get; } = new(
        double.PositiveInfinity,
        new[] { double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity, double.PositiveInfinity },
        Array.Empty<double>(),
        Array.Empty<double>());
}
