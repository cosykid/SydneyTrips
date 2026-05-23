# Bench

A benchmark harness that pits the OR-Tools CP-SAT solver against the custom heuristic over a matrix of synthetic Sydney instances. The output is `REPORT.md` in this folder, which is the source for the **Benchmark results** section of the top-level README.

If you just want the numbers, read [`REPORT.md`](./REPORT.md). The TL;DR: the heuristic beats OR-Tools' best-found solution by 7–26% on 20+ passenger instances inside a 10 second wall-clock budget. Below 10 passengers both solvers reach optimal trivially.

## Running it

```bash
# default: 60 instances × 2 solvers × 10s budget (~20 min upper bound)
dotnet run -c Release --project bench/Trips.Bench

# faster: fewer instances + shorter budget — good for smoke
dotnet run -c Release --project bench/Trips.Bench -- --instances 12 --time-budget-ms 3000

# longer: tighter convergence on the big classes
dotnet run -c Release --project bench/Trips.Bench -- --instances 60 --time-budget-ms 30000 --output bench/REPORT-30s.md
```

Outputs:

- `bench/REPORT.md`  — the human-readable summary with the headline number, per-class table, per-instance table, two embedded mermaid graphs of hand-picked solutions, and ASCII convergence sparklines.
- `bench/results.csv` — the raw row-per-instance dump for spreadsheet-y follow-up. Columns include per-term objective values, OR-Tools status / gap / branches, heuristic iteration count and acceptance rate, and stop counts for both solvers.

## What's in the matrix

The default 60-instance run covers 12 classes:

```
participants:  5,  10, 20, 30
drivers:       2,  3,  5
seeds:         5 per (n, d) combo  ⇒ 4 × 3 × 5 = 60
```

Each instance picks a destination (Palm Beach, Bondi Beach, or Wattamolla in the Royal National Park) and samples participant homes from `sydney-suburbs.json` weighted by population. Travel times are synthetic: haversine distance × a 1.25 congestion multiplier — no Google Routes API calls are made, so the bench is reproducible offline.

See `Trips.Bench/Generator/InstanceGenerator.cs` for the seeding logic and `Trips.Bench/Runner/BenchmarkRunner.cs` for the solver loop.

## Why the numbers look the way they do

- On **small instances** (≤10 passengers) OR-Tools terminates at `Optimal` in under 5 seconds. The heuristic also reaches the optimum trivially. Gap ≈ 0.
- On **medium instances** (20p) OR-Tools times out at `Feasible` and the heuristic is ~7–10% ahead on average.
- On **large instances** (30p) the heuristic widens its lead to 17–26%. The CP-SAT model is correct but its branch-and-bound is dominated by the cheap-insertion + simulated-annealing combo within the 10s budget.

This is the canonical "exact solver vs metaheuristic" story for the size range a group-trip planner actually needs to handle. The README quotes it as the **why we ship both** justification — the CP-SAT solver is there for the guarantee on small inputs and as a baseline to compare against; the heuristic is what runs in production.

The objective function (`drive + stops + walk_pt + arrival_spread + fairness`, weighted) is identical between solvers — both pull from `ObjectiveEvaluator` in `Trips.Optimisation` — so the gap numbers are directly comparable.
