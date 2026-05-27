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
    /// Multiplier applied to <em>driver</em> travel minutes, on top of the user's travel-time weight.
    /// A minute of active driving (a person's time + a car on the road + congestion) is treated as
    /// more costly than a minute of a passenger riding existing public transport — so the same
    /// passenger access-time term (PT, including walks to pickup) competes against a deliberately
    /// heavier driving term.
    ///
    /// <para>Without this, driver minutes and passenger PT minutes are weighed 1:1, so passengers
    /// only shift onto transit once the travel-time weight is cranked to extremes; the premium makes
    /// the trade flip at moderate slider positions. <c>OrToolsSolver</c> reads the same constant so
    /// the two solvers' objectives stay byte-for-byte comparable. Tune here if driving feels too
    /// cheap or too dear.</para>
    /// </summary>
    public const double DriverMinutePremium = 2.0;

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

        // γ · Σ public_transport_access_time(p,n)·assign[d,p,n]
        // This is the passenger's full home→pickup time by public transport, including walking
        // segments. The UI exposes it as one side of the driving-vs-public-transport balance
        // rather than a standalone walking objective.
        double transitAccess = 0.0;
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
            transitAccess += passenger.WalkPtMinsByNodeIndex[localIndex];

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

        // ε · fairness — "share driving time evenly across drivers" (the UI's framing). Defined as
        // the maximum extra driving burden imposed by pickups:
        //
        //     max_d max(0, route_time_d - direct_solo_time_d)
        //
        // Idle drivers count as zero burden. A driver's own home→destination distance is a fact of
        // where they live; fairness should only charge the additional inconvenience created by
        // carpooling. This is what makes a pickup near a driver's natural route attractive: if
        // Steve already drives past Epping, picking up Angela there is a small burden even if his
        // total route time is not the shortest.
        //
        // The journey of how we got here is worth recording because each prior surrogate failed in
        // a different way and the next maintainer will be tempted to revert:
        //
        //   1. max − min over *passenger* journey times — identically zero when every passenger
        //      rides the same driver, so the slider did nothing on trips one car could swallow.
        //   2. Spread of passenger counts per driver — split the *count* but not the *time*; a
        //      3-minute hop and a 60-minute loop are "even" by count, which isn't what users mean.
        //   3. max − min of driver driving minutes — symmetric to (1)'s failure: the solver has two
        //      levers to close the gap, and "pull the short route *up* by detouring it" is often
        //      cheaper than restructuring the long one. Drivers near the destination got dragged
        //      into wasteful loops to "share the load," which the user correctly flagged as absurd.
        //   4. max raw driver driving minutes — avoids the "extend short routes" bug, but mistakes
        //      natural geography for unfairness. Someone who lives far away looks overloaded even
        //      when their pickups are on the way.
        //
        // Min-max extra burden only has the useful lever: reducing the maximum requires moving
        // pickup detour off the most inconvenienced driver. It never rewards extending a short
        // route, and it never treats an idle driver's solo commute as work done for the group. The
        // travel term above is untouched. The CP-SAT model in OrToolsSolver encodes the identical
        // surrogate so the two objective values stay comparable.
        double fairness = 0.0;
        if (input.Drivers.Count > 0)
        {
            double maxBurden = 0.0;
            for (var d = 0; d < input.Drivers.Count; d++)
            {
                if (!driverHasLoad[d]) continue;

                var solo = matrix[input.Drivers[d].OriginNodeIndex, destinationNodeIndex];
                var burden = Math.Max(0.0, driverTravel[d] - solo);
                if (burden > maxBurden) maxBurden = burden;
            }
            fairness = maxBurden;
        }

        var terms = new[]
        {
            weights.DriveTime * DriverMinutePremium * travel,
            weights.StopCount * stops,
            weights.WalkAndPt * transitAccess,
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
