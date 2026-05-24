using Google.OrTools.Sat;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Trips.Core.Abstractions;
using Trips.Core.Domain;
using Trips.Optimisation.Common;

namespace Trips.Optimisation.OrTools;

/// <summary>
/// CP-SAT formulation of the flexible-pickup DARP.
/// <para>
/// Decision variables, mirroring the plan exactly:
/// <list type="bullet">
///   <item><c>assign[d,p,n] ∈ {0,1}</c> — driver d picks up passenger p at node n.</item>
///   <item><c>visit[d,n] ∈ {0,1}</c> — driver d's route visits node n.</item>
///   <item><c>arc[d,i,j] ∈ {0,1}</c> — driver d traverses arc i→j over the augmented node set
///         (origin + pickup nodes + destination).</item>
///   <item><c>arrival[d,n] ∈ int</c> — arrival time at node n along driver d's route, in minutes.</item>
/// </list>
/// </para>
/// <para>
/// Constraints:
/// (1) every passenger assigned exactly once;
/// (2) <c>assign[d,p,n] ≤ visit[d,n]</c>;
/// (3) <c>walk(p,n) ≤ p.walkBudget</c> (walk-budget feasibility) — modelled at *variable creation* by
///     omitting the (p,n) pair when infeasible;
/// (4) per-driver capacity: <c>Σ_p Σ_n assign[d,p,n] ≤ seats[d]</c>;
/// (5) flow conservation: in-degree == out-degree == visit at every node;
///     origin out-degree = 1, destination in-degree = 1 if any visit, else 0;
/// (6) MTZ subtour elimination on arrival times: if <c>arc[d,i,j] = 1</c> then
///     <c>arrival[d,j] ≥ arrival[d,i] + matrix[i,j] − BigM·(1 − arc[d,i,j])</c>;
/// (7) seat capacity per driver is equivalent to (4) — modelled as a single constraint.
/// </para>
/// <para>
/// Objective: <c>α·travel + β·stops + γ·passenger_walk_pt + δ·spread + ε·fairness</c>. CP-SAT works
/// in integers, so weights are scaled by <see cref="WeightScale"/> and travel times are stored as
/// minutes·100 to retain two decimal digits of precision.
/// </para>
/// </summary>
public sealed class OrToolsSolver : ISolver
{
    private const int WeightScale = 1_000;
    private const int TimeScale = 100;
    private const int BigM = ObjectiveEvaluator.BigMArrivalMinutes * TimeScale;

    private readonly SolverOptions _options;
    private readonly ILogger<OrToolsSolver> _logger;

    public OrToolsSolver()
        : this(SolverOptions.Default, NullLogger<OrToolsSolver>.Instance)
    {
    }

    public OrToolsSolver(SolverOptions options, ILogger<OrToolsSolver> logger)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
        _logger = logger ?? NullLogger<OrToolsSolver>.Instance;
    }

    public SolverKind Kind => SolverKind.OrTools;

    /// <summary>
    /// Side-channel: most recent run's CP-SAT status, branches explored, wall-clock time, and
    /// best/bound objectives. Mutated each <see cref="SolveAsync"/> call (single-threaded by
    /// construction). The bench harness reads this immediately after the solver returns.
    /// </summary>
    public OrToolsStats LastStats { get; private set; } = OrToolsStats.Empty;

    public Task<Solution> SolveAsync(SolverInput input, CancellationToken ct)
    {
        ArgumentNullException.ThrowIfNull(input);
        // CP-SAT's Solve() is blocking; offload to a thread pool worker so we honour the async signature.
        return Task.Run(() => SolveSync(input, ct), ct);
    }

    private Solution SolveSync(SolverInput input, CancellationToken ct)
    {
        var destIndex = FindDestinationIndex(input);
        var pickupNodes = EnumeratePickupNodes(input);

        var model = new CpModel();

        // --- Variables ---------------------------------------------------------------------------

        // assign[d,p,n] — only created for (p,n) pairs the passenger considers feasible (walk budget
        // already trimmed by the candidate-node generator). For pairs not in the passenger's
        // candidate set the variable is omitted, which trivially enforces walk-budget feasibility.
        var assign = new BoolVar[input.Drivers.Count, input.Passengers.Count, input.Nodes.Count];
        for (var d = 0; d < input.Drivers.Count; d++)
        {
            for (var p = 0; p < input.Passengers.Count; p++)
            {
                foreach (var n in input.Passengers[p].CandidateNodeIndices)
                {
                    assign[d, p, n] = model.NewBoolVar($"assign_d{d}_p{p}_n{n}");
                }
            }
        }

        // visit[d,n] — only for pickup nodes (drivers always visit their origin and destination).
        var visit = new BoolVar[input.Drivers.Count, input.Nodes.Count];
        for (var d = 0; d < input.Drivers.Count; d++)
        {
            foreach (var n in pickupNodes)
            {
                visit[d, n] = model.NewBoolVar($"visit_d{d}_n{n}");
            }
        }

        // arc[d,i,j] — over the augmented node set (origin + pickup nodes + destination).
        // We index using all node indices but only create variables for the augmented subset per
        // driver (the driver's origin, the pickup nodes, the destination). Self-loops are omitted.
        var arc = new Dictionary<(int d, int i, int j), BoolVar>();
        for (var d = 0; d < input.Drivers.Count; d++)
        {
            var nodes = NodesForDriver(d, input.Drivers[d].OriginNodeIndex, destIndex, pickupNodes);
            foreach (var i in nodes)
            {
                foreach (var j in nodes)
                {
                    if (i == j) continue;
                    // Origin has no incoming arcs; destination has no outgoing arcs.
                    if (j == input.Drivers[d].OriginNodeIndex) continue;
                    if (i == destIndex) continue;
                    arc[(d, i, j)] = model.NewBoolVar($"arc_d{d}_{i}_{j}");
                }
            }
        }

        // arrival[d,n] in minutes·100; bounded by the big-M for the upper limit.
        var arrival = new IntVar[input.Drivers.Count, input.Nodes.Count];
        for (var d = 0; d < input.Drivers.Count; d++)
        {
            foreach (var n in NodesForDriver(d, input.Drivers[d].OriginNodeIndex, destIndex, pickupNodes))
            {
                arrival[d, n] = model.NewIntVar(0, BigM, $"arr_d{d}_n{n}");
            }
            // Origin arrival is anchored at 0.
            model.Add(arrival[d, input.Drivers[d].OriginNodeIndex] == 0);
        }

        // --- Constraints -------------------------------------------------------------------------

        // (1) every passenger assigned exactly once across (driver, node)
        for (var p = 0; p < input.Passengers.Count; p++)
        {
            var literals = new List<BoolVar>();
            for (var d = 0; d < input.Drivers.Count; d++)
            {
                foreach (var n in input.Passengers[p].CandidateNodeIndices)
                {
                    literals.Add(assign[d, p, n]);
                }
            }
            model.AddExactlyOne(literals);
        }

        // (2) Pair of constraints linking assign and visit:
        //   assign[d,p,n] ≤ visit[d,n]       — if a passenger is picked up here, the driver visits.
        //   visit[d,n] ≤ Σ_p assign[d,p,n]   — but the driver doesn't visit a node nobody is at.
        // Without the second half, empty "phantom" stops can appear in the optimum, inflating the
        // stop count and travel time. We enforce both.
        for (var d = 0; d < input.Drivers.Count; d++)
        {
            for (var p = 0; p < input.Passengers.Count; p++)
            {
                foreach (var n in input.Passengers[p].CandidateNodeIndices)
                {
                    model.Add(assign[d, p, n] <= visit[d, n]);
                }
            }
            foreach (var n in pickupNodes)
            {
                var assignSum = LinearExpr.NewBuilder();
                for (var p = 0; p < input.Passengers.Count; p++)
                {
                    foreach (var cn in input.Passengers[p].CandidateNodeIndices)
                    {
                        if (cn == n) assignSum.AddTerm(assign[d, p, n], 1);
                    }
                }
                model.Add(visit[d, n] <= assignSum);
            }
        }

        // (4)/(7) capacity per driver: Σ assign ≤ seats. We also build the per-driver "hasLoad"
        // boolean — 1 iff the driver carries ≥ 1 passenger — which gates routing on demand and the
        // spread/fairness contributions (idle drivers don't count).
        var hasLoadVars = new BoolVar[input.Drivers.Count];
        for (var d = 0; d < input.Drivers.Count; d++)
        {
            var sum = LinearExpr.NewBuilder();
            for (var p = 0; p < input.Passengers.Count; p++)
            {
                foreach (var n in input.Passengers[p].CandidateNodeIndices)
                {
                    sum.AddTerm(assign[d, p, n], 1);
                }
            }
            model.Add(sum <= input.Drivers[d].Seats);

            // hasLoad is 1 iff driver d carries ≥ 1 passenger. Two-way reified.
            var hasLoad = model.NewBoolVar($"has_load_d{d}");
            model.Add(sum >= 1).OnlyEnforceIf(hasLoad);
            model.Add(sum == 0).OnlyEnforceIf(hasLoad.Not());
            hasLoadVars[d] = hasLoad;
        }

        // (5) flow conservation: for each pickup node, in-degree == out-degree == visit[d,n].
        // For the origin, out-degree == 1, in-degree == 0.
        // For the destination, in-degree == 1 if any pickup visited else flexible (we model it as
        // out-flow=0, in-flow= ⟨1 if any visit else 0⟩ via reified constraints).
        for (var d = 0; d < input.Drivers.Count; d++)
        {
            var origin = input.Drivers[d].OriginNodeIndex;

            // pickup nodes
            foreach (var n in pickupNodes)
            {
                var inSum = LinearExpr.NewBuilder();
                var outSum = LinearExpr.NewBuilder();
                foreach (var i in NodesForDriver(d, origin, destIndex, pickupNodes))
                {
                    if (i == n) continue;
                    if (i != destIndex && arc.TryGetValue((d, i, n), out var ain))
                    {
                        inSum.AddTerm(ain, 1);
                    }
                    if (i != origin && arc.TryGetValue((d, n, i), out var aout))
                    {
                        outSum.AddTerm(aout, 1);
                    }
                }
                model.Add(inSum == visit[d, n]);
                model.Add(outSum == visit[d, n]);
            }

            // origin: out-degree == hasLoad — a driver only leaves their origin when they carry
            // someone. Idle drivers stay home and contribute nothing to spread/fairness/travel.
            var originOut = LinearExpr.NewBuilder();
            foreach (var j in NodesForDriver(d, origin, destIndex, pickupNodes))
            {
                if (j == origin) continue;
                if (arc.TryGetValue((d, origin, j), out var a))
                {
                    originOut.AddTerm(a, 1);
                }
            }
            model.Add(originOut == hasLoadVars[d]);

            // destination: in-degree == hasLoad (matched with origin out-degree).
            var destIn = LinearExpr.NewBuilder();
            foreach (var i in NodesForDriver(d, origin, destIndex, pickupNodes))
            {
                if (i == destIndex) continue;
                if (arc.TryGetValue((d, i, destIndex), out var a))
                {
                    destIn.AddTerm(a, 1);
                }
            }
            model.Add(destIn == hasLoadVars[d]);
        }

        // (6) MTZ: when arc[d,i,j] = 1, force arrival[d,j] == arrival[d,i] + travel[i,j] via a pair
        // of big-M inequalities (lower bound + upper bound). The lower-only form is a classical
        // mistake: it lets the solver inflate idle arrivals arbitrarily, which would make the
        // spread/fairness terms trivially zero in the optimum. Pinning equality fixes that.
        for (var d = 0; d < input.Drivers.Count; d++)
        {
            var origin = input.Drivers[d].OriginNodeIndex;
            foreach (var ((dd, i, j), a) in arc)
            {
                if (dd != d) continue;
                if (j == origin) continue;
                var tij = (int)Math.Round(input.TravelMatrix[i, j] * TimeScale);
                model.Add(arrival[d, j] >= arrival[d, i] + tij - BigM * (1 - (LinearExpr)a));
                model.Add(arrival[d, j] <= arrival[d, i] + tij + BigM * (1 - (LinearExpr)a));
            }
        }

        // --- Objective ---------------------------------------------------------------------------

        // Build the full objective inline — combining 5 terms with their scaled weights into one
        // LinearExprBuilder. We avoid building separate sub-expressions and re-multiplying because
        // LinearExprBuilder.AddTerm(LinearExpr,long) does NOT collapse nested builders; instead it
        // would treat the inner builder as a single literal which yields a silently-wrong objective.
        var totalObj = LinearExpr.NewBuilder();
        var w0 = (long)Math.Round(input.Weights.DriveTime * WeightScale);
        var w1 = (long)Math.Round(input.Weights.StopCount * WeightScale);
        var w2 = (long)Math.Round(input.Weights.WalkAndPt * WeightScale);
        var w3 = (long)Math.Round(input.Weights.ArrivalSpread * WeightScale);
        var w4 = (long)Math.Round(input.Weights.Fairness * WeightScale);

        // Travel term: Σ over arcs of arc[d,i,j] · matrix[i,j]·TimeScale · w0
        foreach (var ((d, i, j), a) in arc)
        {
            var tij = (long)Math.Round(input.TravelMatrix[i, j] * TimeScale);
            totalObj.AddTerm(a, tij * w0);
        }

        // Stops term: Σ visit[d,n] · TimeScale · w1 (only pickup nodes counted)
        for (var d = 0; d < input.Drivers.Count; d++)
        {
            foreach (var n in pickupNodes)
            {
                totalObj.AddTerm(visit[d, n], (long)TimeScale * w1);
            }
        }

        // Walk term: Σ assign[d,p,n] · walkPt(p,n)·TimeScale · w2
        for (var d = 0; d < input.Drivers.Count; d++)
        {
            for (var p = 0; p < input.Passengers.Count; p++)
            {
                for (var k = 0; k < input.Passengers[p].CandidateNodeIndices.Count; k++)
                {
                    var n = input.Passengers[p].CandidateNodeIndices[k];
                    var w = (long)input.Passengers[p].WalkPtMinsByNodeIndex[k] * TimeScale;
                    totalObj.AddTerm(assign[d, p, n], w * w2);
                }
            }
        }

        // Spread term: arrivalSpread = max − min of *passenger* destination arrivals. Since every
        // passenger arrives with their driver, the per-passenger max/min equals the per-driver
        // max/min restricted to drivers carrying ≥ 1 passenger. Big-M slackener tied to hasLoad:
        // when a driver carries nobody, their arrival doesn't bind the spread variables.
        var maxArr = model.NewIntVar(0, BigM, "max_arr");
        var minArr = model.NewIntVar(0, BigM, "min_arr");
        var spread = model.NewIntVar(0, BigM, "spread");
        for (var d = 0; d < input.Drivers.Count; d++)
        {
            model.Add(maxArr >= arrival[d, destIndex] - BigM * (1 - (LinearExpr)hasLoadVars[d]));
            model.Add(minArr <= arrival[d, destIndex] + BigM * (1 - (LinearExpr)hasLoadVars[d]));
        }
        model.Add(spread == maxArr - minArr);

        // Fairness term: "Share driving time evenly across drivers" (the UI's framing). The old
        // formulation was max_passenger journey-time minus min_passenger journey-time — which is
        // identically zero when every passenger rides the same driver, so it gave the solver no
        // reason to use more than one car. The user-facing slider then did nothing on trips with
        // enough seats in one car.
        //
        // Now: penalise the spread of load (passengers carried) across drivers. With N passengers
        // and K capacious drivers, the minimiser of maxLoad-minLoad is ⌈N/K⌉ - ⌊N/K⌋ (the
        // balanced split), so cranking this slider pushes the solver toward distributing pickups.
        // Multiplied by TimeScale so this term lives in the same scaled units as the others.
        // Per-driver load = sum of assign[d,p,n] over all (p,n). Bound each load var to the actual
        // sum so CP-SAT propagates it through the AddMaxEquality / AddMinEquality below.
        var loadVars = new IntVar[input.Drivers.Count];
        for (var d = 0; d < input.Drivers.Count; d++)
        {
            var load = model.NewIntVar(0, input.Passengers.Count, $"load_d{d}");
            var loadSum = LinearExpr.NewBuilder();
            for (var p = 0; p < input.Passengers.Count; p++)
            {
                foreach (var n in input.Passengers[p].CandidateNodeIndices)
                {
                    loadSum.AddTerm(assign[d, p, n], 1);
                }
            }
            model.Add(load == loadSum);
            loadVars[d] = load;
        }
        var maxLoad = model.NewIntVar(0, input.Passengers.Count, "max_load");
        var minLoad = model.NewIntVar(0, input.Passengers.Count, "min_load");
        model.AddMaxEquality(maxLoad, loadVars);
        model.AddMinEquality(minLoad, loadVars);
        var loadSpread = model.NewIntVar(0, input.Passengers.Count, "load_spread");
        model.Add(loadSpread == maxLoad - minLoad);

        // Spread already lives in scaled time units (TimeScale). loadSpread is a passenger count;
        // multiply by TimeScale so a 1-passenger imbalance compares to a 1-minute drive overhead
        // at unit weight — i.e. cranking Fairness to 1.0 says "I'd happily pay an extra minute of
        // drive time to balance one passenger off a single driver". That's the natural anchor.
        totalObj.AddTerm(spread, w3);
        totalObj.AddTerm(loadSpread, (long)TimeScale * w4);
        model.Minimize(totalObj);

        // --- Warm-start hint (what-if re-optimisation) -------------------------------------------
        // When supplied, hint the CP-SAT search toward the locked plan. We pin assign[d,p,n]=1 for
        // each passenger's prior pickup, visit[d,n]=1 for every prior stop, and arc[d,i,j]=1 along
        // the prior driver sequence. CP-SAT treats hints as soft (initial solution to repair), so
        // dropping/adding a passenger or changing weights can still shift the optimum.
        if (input.WarmStartHint is { } hint)
        {
            ApplyWarmStartHint(model, hint, assign, visit, arc, input.Drivers.Count, input.Drivers, destIndex, pickupNodes);
        }

        // --- Solve -------------------------------------------------------------------------------

        var solver = new CpSolver();
        var seconds = Math.Max(0.05, _options.TimeBudgetMs / 1000.0);
        // Single-worker search is the only way CP-SAT is reproducible across runs. With multiple
        // workers, the search races to find a solution and the "best so far" returned at the time
        // budget depends on wall-clock order — so re-clicking "Re-plan" with the same input could
        // alternate between (e.g.) "steve picks up at Epping, 22 min" and "steve detours to
        // Randwick, 81 min". For the problem sizes we see (≤ 20 nodes, ≤ 10 drivers) CP-SAT
        // proves optimality in <100ms even on one core, so the latency cost is zero in practice.
        // If we ever ship a size class where this hurts, swap to `interleave_search:true` with
        // bumped workers — that's deterministic but parallel; or set a high enough budget to
        // always prove optimal (gap=0 is reproducible no matter how many workers).
        solver.StringParameters =
            $"max_time_in_seconds:{seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}," +
            $"num_search_workers:1," +
            $"random_seed:{_options.RandomSeed}," +
            (_options.LogProgress ? "log_search_progress:true" : "log_search_progress:false");

        if (ct.IsCancellationRequested)
        {
            ct.ThrowIfCancellationRequested();
        }
        var status = solver.Solve(model);
        _logger.LogInformation("OR-Tools status={Status} obj={Obj} wall={Wall}s", status, solver.ObjectiveValue, solver.WallTime());
        LastStats = new OrToolsStats(
            Status: status,
            BestObjective: status is CpSolverStatus.Optimal or CpSolverStatus.Feasible ? solver.ObjectiveValue / 100_000.0 : double.PositiveInfinity,
            BestBound: solver.BestObjectiveBound / 100_000.0,
            Branches: solver.NumBranches(),
            WallClock: TimeSpan.FromSeconds(solver.WallTime()));

        if (status != CpSolverStatus.Optimal && status != CpSolverStatus.Feasible)
        {
            // No solution found within budget — emit a degenerate solution where every passenger
            // is assigned to driver 0 at their first candidate node (best-effort fallback).
            return BuildFallback(input, destIndex);
        }

        // Extract solution
        var routesPerDriver = new IReadOnlyList<int>[input.Drivers.Count];
        for (var d = 0; d < input.Drivers.Count; d++)
        {
            routesPerDriver[d] = ExtractRoute(solver, arc, d, input.Drivers[d].OriginNodeIndex, destIndex);
        }
        var nodeChoice = new int[input.Passengers.Count];
        var driverChoice = new int[input.Passengers.Count];
        for (var p = 0; p < input.Passengers.Count; p++)
        {
            (driverChoice[p], nodeChoice[p]) = FindAssignment(solver, assign, input, p);
        }

        return SolutionBuilder.Build(input, "OR-Tools", routesPerDriver, nodeChoice, driverChoice, destIndex);
    }

    /// <summary>
    /// Feed a <see cref="WarmStartHint"/> into the CP-SAT model via <c>AddHint</c>. Variables not
    /// mentioned by the hint default to "unhinted" — the solver explores them freely. Indices that
    /// reference variables we never created (e.g. a passenger's prior pickup node when the passenger
    /// has been dropped, or a hint assignment whose passenger has been removed entirely) are skipped.
    /// </summary>
    private static void ApplyWarmStartHint(
        CpModel model,
        WarmStartHint hint,
        BoolVar[,,] assign,
        BoolVar[,] visit,
        Dictionary<(int d, int i, int j), BoolVar> arc,
        int driverCount,
        IReadOnlyList<SolverDriver> drivers,
        int destIndex,
        IReadOnlyList<int> pickupNodes)
    {
        var pickupSet = new HashSet<int>(pickupNodes);
        var hintedAssign = new HashSet<(int d, int p, int n)>();
        var hintedVisit = new HashSet<(int d, int n)>();

        foreach (var a in hint.Assignments)
        {
            if (a.DriverIndex < 0 || a.DriverIndex >= driverCount) continue;
            if (a.PassengerIndex < 0 || a.PassengerIndex >= assign.GetLength(1)) continue;
            if (a.NodeIndex < 0 || a.NodeIndex >= assign.GetLength(2)) continue;
            var av = assign[a.DriverIndex, a.PassengerIndex, a.NodeIndex];
            if (av is null) continue; // (p,n) pair wasn't created (walk-budget infeasible)
            model.AddHint(av, 1);
            hintedAssign.Add((a.DriverIndex, a.PassengerIndex, a.NodeIndex));
            if (pickupSet.Contains(a.NodeIndex))
            {
                var vv = visit[a.DriverIndex, a.NodeIndex];
                if (vv is not null && hintedVisit.Add((a.DriverIndex, a.NodeIndex)))
                {
                    model.AddHint(vv, 1);
                }
            }
        }

        for (var d = 0; d < hint.DriverSequences.Count && d < driverCount; d++)
        {
            var seq = hint.DriverSequences[d];
            if (seq.Count == 0) continue;
            var origin = drivers[d].OriginNodeIndex;
            var prev = origin;
            foreach (var n in seq)
            {
                if (arc.TryGetValue((d, prev, n), out var arcVar))
                {
                    model.AddHint(arcVar, 1);
                }
                if (pickupSet.Contains(n) && hintedVisit.Add((d, n)))
                {
                    var vv = visit[d, n];
                    if (vv is not null) model.AddHint(vv, 1);
                }
                prev = n;
            }
            // Close the route back to destination.
            if (arc.TryGetValue((d, prev, destIndex), out var arcToDest))
            {
                model.AddHint(arcToDest, 1);
            }
        }
    }

    private static IReadOnlyList<int> ExtractRoute(
        CpSolver solver,
        Dictionary<(int d, int i, int j), BoolVar> arcs,
        int driverIndex,
        int origin,
        int destIndex)
    {
        var route = new List<int>();
        var visited = new HashSet<int> { origin };
        var current = origin;
        // Walk the arc graph from origin to destination. Defensive cap so we can't loop forever
        // if the solver returned anything pathological.
        for (var step = 0; step < arcs.Count; step++)
        {
            int? next = null;
            foreach (var ((d, i, j), a) in arcs)
            {
                if (d != driverIndex || i != current) continue;
                if (solver.Value(a) == 1)
                {
                    next = j;
                    break;
                }
            }
            if (next is null || next == destIndex) break;
            if (!visited.Add(next.Value)) break;
            route.Add(next.Value);
            current = next.Value;
        }
        return route;
    }

    private static (int driver, int node) FindAssignment(CpSolver solver, BoolVar[,,] assign, SolverInput input, int p)
    {
        for (var d = 0; d < input.Drivers.Count; d++)
        {
            foreach (var n in input.Passengers[p].CandidateNodeIndices)
            {
                if (solver.Value(assign[d, p, n]) == 1)
                {
                    return (d, n);
                }
            }
        }
        // Shouldn't happen given the AddExactlyOne constraint, but fall back to driver 0 + first node.
        return (0, input.Passengers[p].CandidateNodeIndices[0]);
    }

    private Solution BuildFallback(SolverInput input, int destIndex)
    {
        _logger.LogWarning("OR-Tools produced no feasible solution; emitting fallback (cheapest-insertion construction).");
        // Cheapest-insertion construction — same as the heuristic's bootstrap. Respects capacity.
        var driverCount = input.Drivers.Count;
        var routesPerDriver = new List<List<int>>(driverCount);
        var driverLoad = new int[driverCount];
        for (var d = 0; d < driverCount; d++) routesPerDriver.Add(new List<int>());
        var nodeChoice = new int[input.Passengers.Count];
        var driverChoice = new int[input.Passengers.Count];

        for (var p = 0; p < input.Passengers.Count; p++)
        {
            var passenger = input.Passengers[p];
            // Cheapest candidate by personal walk-PT cost
            var cheapestLocal = 0;
            for (var k = 1; k < passenger.WalkPtMinsByNodeIndex.Count; k++)
            {
                if (passenger.WalkPtMinsByNodeIndex[k] < passenger.WalkPtMinsByNodeIndex[cheapestLocal])
                {
                    cheapestLocal = k;
                }
            }
            var cheapestNode = passenger.CandidateNodeIndices[cheapestLocal];
            var bestDriver = -1;
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
            if (bestDriver < 0) bestDriver = 0; // last-resort capacity violation
            driverChoice[p] = bestDriver;
            nodeChoice[p] = cheapestNode;
            driverLoad[bestDriver]++;
            CheapestInsert(routesPerDriver[bestDriver], cheapestNode, input, input.Drivers[bestDriver].OriginNodeIndex, destIndex);
        }

        var routes = new IReadOnlyList<int>[driverCount];
        for (var d = 0; d < driverCount; d++) routes[d] = routesPerDriver[d];
        return SolutionBuilder.Build(input, "OR-Tools-fallback", routes, nodeChoice, driverChoice, destIndex);
    }

    private static void CheapestInsert(List<int> sequence, int node, SolverInput input, int origin, int destIndex)
    {
        if (sequence.Contains(node)) return;
        if (sequence.Count == 0) { sequence.Add(node); return; }
        var bestPos = 0;
        var bestCost = double.PositiveInfinity;
        for (var pos = 0; pos <= sequence.Count; pos++)
        {
            var prev = pos == 0 ? origin : sequence[pos - 1];
            var next = pos == sequence.Count ? destIndex : sequence[pos];
            var oldEdge = input.TravelMatrix[prev, next];
            var newCost = input.TravelMatrix[prev, node] + input.TravelMatrix[node, next] - oldEdge;
            if (newCost < bestCost) { bestCost = newCost; bestPos = pos; }
        }
        sequence.Insert(bestPos, node);
    }

    private static int FindDestinationIndex(SolverInput input)
    {
        // The destination node has CandidateNodeId == null and is not a driver origin.
        var driverOrigins = new HashSet<int>(input.Drivers.Select(d => d.OriginNodeIndex));
        for (var i = 0; i < input.Nodes.Count; i++)
        {
            var node = input.Nodes[i];
            if (node.CandidateNodeId is null && !driverOrigins.Contains(i))
            {
                return i;
            }
        }
        throw new InvalidOperationException("SolverInput.Nodes does not contain a destination row.");
    }

    private static IReadOnlyList<int> EnumeratePickupNodes(SolverInput input)
    {
        // Pickup nodes = union of passengers' candidate node indices.
        var set = new HashSet<int>();
        foreach (var passenger in input.Passengers)
        {
            foreach (var n in passenger.CandidateNodeIndices) set.Add(n);
        }
        return set.OrderBy(x => x).ToArray();
    }

    private static IEnumerable<int> NodesForDriver(int d, int origin, int destIndex, IReadOnlyList<int> pickups)
    {
        yield return origin;
        foreach (var n in pickups) yield return n;
        yield return destIndex;
    }

}

/// <summary>
/// Returned alongside the <see cref="Solution"/> via the <see cref="OrToolsSolver.LastStats"/>
/// side-channel. Captures CP-SAT-specific run metrics for the benchmark report (status, branches,
/// gap to best bound).
/// </summary>
public sealed record OrToolsStats(
    CpSolverStatus Status,
    double BestObjective,
    double BestBound,
    long Branches,
    TimeSpan WallClock)
{
    public static OrToolsStats Empty { get; } = new(
        Status: CpSolverStatus.Unknown,
        BestObjective: double.PositiveInfinity,
        BestBound: double.NegativeInfinity,
        Branches: 0,
        WallClock: TimeSpan.Zero);

    /// <summary>
    /// Relative optimality gap: <c>(obj − bound) / |obj|</c>. 0 when status==Optimal.
    /// </summary>
    public double Gap => double.IsInfinity(BestObjective) || BestObjective == 0
        ? double.PositiveInfinity
        : Math.Max(0, (BestObjective - BestBound) / Math.Abs(BestObjective));
}
