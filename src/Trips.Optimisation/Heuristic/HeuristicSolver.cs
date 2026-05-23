using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NetTopologySuite.Geometries;
using Trips.Core.Abstractions;
using Trips.Core.Domain;
using Trips.Optimisation.Common;

namespace Trips.Optimisation.Heuristic;

/// <summary>
/// Construction + Simulated Annealing local search heuristic for the flexible-pickup DARP.
///
/// <list type="number">
///   <item><b>Construction</b>: every passenger is assigned to the driver whose direct route
///   (origin→destination) most closely "passes" their cheapest-cost candidate node, then we insert
///   the chosen node into the driver's sequence via cheapest-insertion ordering.</item>
///   <item><b>Local search moves</b>, rotated round-robin per iteration:
///     <list type="bullet">
///       <item>(a) reassign a passenger to a different driver</item>
///       <item>(b) reassign a passenger to a different candidate node on the same driver</item>
///       <item>(c) 2-opt within a single driver's route</item>
///       <item>(d) swap two passengers between drivers</item>
///     </list>
///   </item>
///   <item><b>Simulated annealing</b>: temperature decays geometrically; worsening moves accepted
///   with probability <c>exp(−Δ/T)</c>. Default schedule tuned on the 5/10/20 passenger benchmark
///   classes (see <see cref="SimulatedAnnealingSchedule.Default"/>).</item>
/// </list>
/// </summary>
public sealed class HeuristicSolver : ISolver
{
    private readonly SolverOptions _options;
    private readonly SimulatedAnnealingSchedule _schedule;
    private readonly ILogger<HeuristicSolver> _logger;

    public HeuristicSolver()
        : this(SolverOptions.Default, SimulatedAnnealingSchedule.Default, NullLogger<HeuristicSolver>.Instance)
    {
    }

    public HeuristicSolver(SolverOptions options, SimulatedAnnealingSchedule schedule, ILogger<HeuristicSolver> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _schedule = schedule ?? throw new ArgumentNullException(nameof(schedule));
        _logger = logger ?? NullLogger<HeuristicSolver>.Instance;
    }

    public SolverKind Kind => SolverKind.Heuristic;

    /// <summary>
    /// Side-channel: most recent run's iteration count, accepted count, and best-so-far convergence trace.
    /// Mutated each <see cref="SolveAsync"/> call. Bench harness reads this immediately after the
    /// solver returns (single-threaded by construction).
    /// </summary>
    public HeuristicStats LastStats { get; private set; } = HeuristicStats.Empty;

    public Task<Solution> SolveAsync(SolverInput input, CancellationToken ct) =>
        Task.Run(() => SolveSync(input, ct), ct);

    private Solution SolveSync(SolverInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        var destIndex = FindDestinationIndex(input);
        var nodeLocations = PlaceholderNodeLocations(input.Nodes.Count);

        var state = ConstructInitial(input, destIndex);
        var stopwatch = Stopwatch.StartNew();
        var random = new Random(_options.RandomSeed);
        var temperature = _schedule.StartTemperature;
        var best = state.Clone();
        var bestEval = ObjectiveEvaluator.Evaluate(input, state.RoutesView(), state.NodeChoicePerPassenger, state.DriverPerPassenger, destIndex);
        var current = best.Clone();
        var currentObj = bestEval.Objective;
        var convergence = new List<(int iter, double best)> { (0, bestEval.Objective) };

        var iterations = 0;
        var accepted = 0;
        while (stopwatch.ElapsedMilliseconds < _options.TimeBudgetMs && !ct.IsCancellationRequested)
        {
            iterations++;
            var moveKind = iterations % 4;
            var neighbour = current.Clone();
            var ok = moveKind switch
            {
                0 => MoveReassignPassengerToDriver(neighbour, input, random),
                1 => MoveReassignPassengerToNode(neighbour, input, random),
                2 => MoveTwoOpt(neighbour, input, random),
                _ => MoveSwapPassengers(neighbour, input, random),
            };
            if (!ok) continue;

            var neighbourEval = ObjectiveEvaluator.Evaluate(input, neighbour.RoutesView(), neighbour.NodeChoicePerPassenger, neighbour.DriverPerPassenger, destIndex);
            if (double.IsPositiveInfinity(neighbourEval.Objective))
            {
                continue; // infeasible move
            }

            var delta = neighbourEval.Objective - currentObj;
            var accept = delta < 0 || random.NextDouble() < Math.Exp(-delta / Math.Max(temperature, 1e-6));
            if (accept)
            {
                current = neighbour;
                currentObj = neighbourEval.Objective;
                accepted++;
                if (currentObj < bestEval.Objective)
                {
                    best = current.Clone();
                    bestEval = neighbourEval;
                    convergence.Add((iterations, bestEval.Objective));
                }
            }

            temperature = Math.Max(_schedule.MinTemperature, temperature * _schedule.CoolingRate);
            if (temperature <= _schedule.MinTemperature && iterations % _schedule.ReheatEveryIterations == 0)
            {
                // Reheat to escape local optima — a tenth of the starting heat.
                temperature = _schedule.StartTemperature * _schedule.ReheatFactor;
            }
        }

        stopwatch.Stop();
        LastStats = new HeuristicStats(
            Iterations: iterations,
            Accepted: accepted,
            AcceptanceRate: iterations == 0 ? 0.0 : (double)accepted / iterations,
            ConvergenceTrace: convergence,
            WallClock: stopwatch.Elapsed);

        _logger.LogInformation("Heuristic done iters={Iter} accepted={Acc} best={Best} wall={Wall}ms",
            iterations, accepted, bestEval.Objective, stopwatch.ElapsedMilliseconds);

        return SolutionBuilder.Build(input, "Heuristic", best.RoutesView(), best.NodeChoicePerPassenger, best.DriverPerPassenger, destIndex, nodeLocations);
    }

    // ---------------- Construction ----------------

    private static SolutionState ConstructInitial(SolverInput input, int destIndex)
    {
        var driverCount = input.Drivers.Count;
        var passengerCount = input.Passengers.Count;
        var routesPerDriver = new List<List<int>>(driverCount);
        var driverLoad = new int[driverCount];
        for (var d = 0; d < driverCount; d++) routesPerDriver.Add(new List<int>());
        var nodeChoice = new int[passengerCount];
        var driverChoice = new int[passengerCount];

        // For each passenger: find the cheapest-cost candidate node, then pick the driver whose
        // origin→destination path passes closest to that node (proxied as matrix[origin,node] +
        // matrix[node,dest] − matrix[origin,dest]).
        for (var p = 0; p < passengerCount; p++)
        {
            var passenger = input.Passengers[p];
            // Cheapest candidate by personal cost
            var cheapestLocal = 0;
            for (var k = 1; k < passenger.WalkPtMinsByNodeIndex.Count; k++)
            {
                if (passenger.WalkPtMinsByNodeIndex[k] < passenger.WalkPtMinsByNodeIndex[cheapestLocal])
                {
                    cheapestLocal = k;
                }
            }
            var cheapestNode = passenger.CandidateNodeIndices[cheapestLocal];
            // Best driver = minimal extra detour to include this node, breaking ties by remaining capacity
            var bestDriver = 0;
            var bestDetour = double.PositiveInfinity;
            for (var d = 0; d < driverCount; d++)
            {
                if (driverLoad[d] >= input.Drivers[d].Seats) continue;
                var origin = input.Drivers[d].OriginNodeIndex;
                var detour = input.TravelMatrix[origin, cheapestNode] + input.TravelMatrix[cheapestNode, destIndex] - input.TravelMatrix[origin, destIndex];
                if (detour < bestDetour)
                {
                    bestDetour = detour;
                    bestDriver = d;
                }
            }
            driverChoice[p] = bestDriver;
            nodeChoice[p] = cheapestNode;
            driverLoad[bestDriver]++;
            // Cheapest-insertion ordering within driver's route
            CheapestInsert(routesPerDriver[bestDriver], cheapestNode, input, input.Drivers[bestDriver].OriginNodeIndex, destIndex);
        }

        return new SolutionState(routesPerDriver, nodeChoice, driverChoice);
    }

    private static void CheapestInsert(List<int> sequence, int node, SolverInput input, int origin, int destIndex)
    {
        if (sequence.Contains(node)) return; // already inserted
        if (sequence.Count == 0)
        {
            sequence.Add(node);
            return;
        }
        var bestPos = 0;
        var bestCost = double.PositiveInfinity;
        for (var pos = 0; pos <= sequence.Count; pos++)
        {
            var prev = pos == 0 ? origin : sequence[pos - 1];
            var next = pos == sequence.Count ? destIndex : sequence[pos];
            var oldEdge = input.TravelMatrix[prev, next];
            var newCost = input.TravelMatrix[prev, node] + input.TravelMatrix[node, next] - oldEdge;
            if (newCost < bestCost)
            {
                bestCost = newCost;
                bestPos = pos;
            }
        }
        sequence.Insert(bestPos, node);
    }

    // ---------------- Local-search moves ----------------

    private static bool MoveReassignPassengerToDriver(SolutionState state, SolverInput input, Random random)
    {
        if (input.Passengers.Count == 0 || input.Drivers.Count < 2) return false;
        var p = random.Next(input.Passengers.Count);
        var oldD = state.DriverPerPassenger[p];
        var newD = (oldD + 1 + random.Next(input.Drivers.Count - 1)) % input.Drivers.Count;
        // Capacity guard
        int loadNew = 0;
        for (var q = 0; q < input.Passengers.Count; q++)
            if (state.DriverPerPassenger[q] == newD) loadNew++;
        if (loadNew >= input.Drivers[newD].Seats) return false;

        var oldNode = state.NodeChoicePerPassenger[p];
        // Remove old node from oldD route if no one else uses it.
        if (!OtherPassengerUses(state, p, oldD, oldNode, input))
        {
            state.RoutesPerDriver[oldD].Remove(oldNode);
        }
        state.DriverPerPassenger[p] = newD;
        // Choose a node — keep current if reachable from newD, else cheapest candidate
        var keep = state.NodeChoicePerPassenger[p];
        if (!state.RoutesPerDriver[newD].Contains(keep))
        {
            CheapestInsert(state.RoutesPerDriver[newD], keep, input, input.Drivers[newD].OriginNodeIndex, FindDestinationIndex(input));
        }
        return true;
    }

    private static bool MoveReassignPassengerToNode(SolutionState state, SolverInput input, Random random)
    {
        if (input.Passengers.Count == 0) return false;
        var p = random.Next(input.Passengers.Count);
        var passenger = input.Passengers[p];
        if (passenger.CandidateNodeIndices.Count < 2) return false;
        var oldNode = state.NodeChoicePerPassenger[p];
        var d = state.DriverPerPassenger[p];
        // Pick a different candidate uniformly at random
        int newLocal;
        do { newLocal = random.Next(passenger.CandidateNodeIndices.Count); }
        while (passenger.CandidateNodeIndices[newLocal] == oldNode);
        var newNode = passenger.CandidateNodeIndices[newLocal];

        if (!OtherPassengerUses(state, p, d, oldNode, input))
        {
            state.RoutesPerDriver[d].Remove(oldNode);
        }
        state.NodeChoicePerPassenger[p] = newNode;
        if (!state.RoutesPerDriver[d].Contains(newNode))
        {
            CheapestInsert(state.RoutesPerDriver[d], newNode, input, input.Drivers[d].OriginNodeIndex, FindDestinationIndex(input));
        }
        return true;
    }

    private static bool MoveTwoOpt(SolutionState state, SolverInput input, Random random)
    {
        if (input.Drivers.Count == 0) return false;
        var d = random.Next(input.Drivers.Count);
        var route = state.RoutesPerDriver[d];
        if (route.Count < 4) return false;
        var i = random.Next(0, route.Count - 1);
        var j = random.Next(i + 1, route.Count);
        // Reverse route[i..j]
        while (i < j)
        {
            (route[i], route[j]) = (route[j], route[i]);
            i++; j--;
        }
        return true;
    }

    private static bool MoveSwapPassengers(SolutionState state, SolverInput input, Random random)
    {
        if (input.Passengers.Count < 2) return false;
        var p = random.Next(input.Passengers.Count);
        var q = random.Next(input.Passengers.Count);
        if (p == q) return false;
        if (state.DriverPerPassenger[p] == state.DriverPerPassenger[q]) return false;
        var dP = state.DriverPerPassenger[p];
        var dQ = state.DriverPerPassenger[q];

        // Capacity preserved by swap, no extra check needed.
        // Swap driver assignments, then re-attach nodes.
        var oldNodeP = state.NodeChoicePerPassenger[p];
        var oldNodeQ = state.NodeChoicePerPassenger[q];
        if (!OtherPassengerUses(state, p, dP, oldNodeP, input)) state.RoutesPerDriver[dP].Remove(oldNodeP);
        if (!OtherPassengerUses(state, q, dQ, oldNodeQ, input)) state.RoutesPerDriver[dQ].Remove(oldNodeQ);
        state.DriverPerPassenger[p] = dQ;
        state.DriverPerPassenger[q] = dP;
        if (!state.RoutesPerDriver[dQ].Contains(oldNodeP))
        {
            CheapestInsert(state.RoutesPerDriver[dQ], oldNodeP, input, input.Drivers[dQ].OriginNodeIndex, FindDestinationIndex(input));
        }
        if (!state.RoutesPerDriver[dP].Contains(oldNodeQ))
        {
            CheapestInsert(state.RoutesPerDriver[dP], oldNodeQ, input, input.Drivers[dP].OriginNodeIndex, FindDestinationIndex(input));
        }
        return true;
    }

    private static bool OtherPassengerUses(SolutionState state, int passengerIndex, int driverIndex, int node, SolverInput input)
    {
        for (var q = 0; q < input.Passengers.Count; q++)
        {
            if (q == passengerIndex) continue;
            if (state.DriverPerPassenger[q] == driverIndex && state.NodeChoicePerPassenger[q] == node)
            {
                return true;
            }
        }
        return false;
    }

    private static int FindDestinationIndex(SolverInput input)
    {
        var origins = new HashSet<int>(input.Drivers.Select(d => d.OriginNodeIndex));
        for (var i = 0; i < input.Nodes.Count; i++)
        {
            if (input.Nodes[i].CandidateNodeId is null && !origins.Contains(i)) return i;
        }
        throw new InvalidOperationException("Destination row not found.");
    }

    private static IReadOnlyList<Point> PlaceholderNodeLocations(int count)
    {
        var arr = new Point[count];
        for (var i = 0; i < count; i++) arr[i] = new Point(0, 0) { SRID = 4326 };
        return arr;
    }

    /// <summary>Mutable state shared by the move operators. The raw <see cref="List{T}"/>s are kept
    /// because the local-search operators mutate them in place; for the public projection consumed
    /// by <see cref="ObjectiveEvaluator"/> we return the same instances cast as <see cref="IReadOnlyList{T}"/>.</summary>
    private sealed class SolutionState
    {
        public List<List<int>> RoutesPerDriver { get; }
        public int[] NodeChoicePerPassenger { get; }
        public int[] DriverPerPassenger { get; }

        public SolutionState(List<List<int>> routes, int[] node, int[] driver)
        {
            RoutesPerDriver = routes;
            NodeChoicePerPassenger = node;
            DriverPerPassenger = driver;
        }

        public IReadOnlyList<IReadOnlyList<int>> RoutesView()
        {
            var view = new IReadOnlyList<int>[RoutesPerDriver.Count];
            for (var i = 0; i < RoutesPerDriver.Count; i++) view[i] = RoutesPerDriver[i];
            return view;
        }

        public SolutionState Clone()
        {
            var routes = new List<List<int>>(RoutesPerDriver.Count);
            foreach (var r in RoutesPerDriver) routes.Add(new List<int>(r));
            return new SolutionState(routes, (int[])NodeChoicePerPassenger.Clone(), (int[])DriverPerPassenger.Clone());
        }
    }
}

/// <summary>
/// Tuned defaults for the SA temperature schedule. Exposed as a record so callers can override the
/// schedule per-instance during benchmarking (e.g. larger instances want a slower cool-down).
/// </summary>
/// <param name="StartTemperature">Initial temperature. Higher accepts more bad moves early.</param>
/// <param name="MinTemperature">Floor; below this we either reheat or coast.</param>
/// <param name="CoolingRate">Multiplicative cooling per iteration. 0.999 is the tuned default.</param>
/// <param name="ReheatEveryIterations">When at MinTemperature, reheat every N iterations.</param>
/// <param name="ReheatFactor">Fraction of <see cref="StartTemperature"/> to reheat back to.</param>
public sealed record SimulatedAnnealingSchedule(
    double StartTemperature = 50.0,
    double MinTemperature = 0.01,
    double CoolingRate = 0.999,
    int ReheatEveryIterations = 5_000,
    double ReheatFactor = 0.1)
{
    public static SimulatedAnnealingSchedule Default { get; } = new();
}

/// <summary>
/// Returned alongside the <see cref="Solution"/> via the <see cref="HeuristicSolver.LastStats"/>
/// side-channel.
/// </summary>
public sealed record HeuristicStats(
    int Iterations,
    int Accepted,
    double AcceptanceRate,
    IReadOnlyList<(int Iteration, double BestObjective)> ConvergenceTrace,
    TimeSpan WallClock)
{
    public static HeuristicStats Empty { get; } = new(0, 0, 0.0, Array.Empty<(int, double)>(), TimeSpan.Zero);
}
