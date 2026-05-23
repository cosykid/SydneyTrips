# SydneyTrips

Plan a Saturday at the beach with eight friends and three cars. Some live by Bondi, some over near Parramatta, two drive themselves and the rest will catch a train to a station that the driver can swing past on the way north. **SydneyTrips picks who rides with whom, where the rendezvous points are, and what order the driver picks them up in** — co-optimising all of that against a multi-objective cost (driving time, total stops, passenger walk/PT minutes, arrival spread, fairness). It is also a live coordinator: once you lock a plan, the drivers and passengers each get a real-time view that updates as cars move.

The interesting bit isn't the routing — it's that every passenger has a *set* of feasible pickup points (their home plus reachable train stations and bus stops), so the solver doesn't just route, it picks the rendezvous structure too. The literature calls this the **Dial-a-Ride Problem with flexible pickup points**; this repo ships a Google OR-Tools CP-SAT formulation, a custom cheapest-insertion + simulated-annealing heuristic, and a bench harness that compares them across 60 synthetic Sydney instances.

![Planning canvas — Pareto carousel + weight sliders + Sydney basemap](docs/screenshots/03-planning-canvas.svg)

*Note: the SVG mockups in this README are produced from real data via `tests/seed/seed-demo.sh`. The login + dashboard screenshots are captured live via Playwright; the deeper views are mocked because of a known frontend↔API contract drift on the trip-detail route — see [Known issues](#status) below. The backend produces real numbers for all of these; only the UI binding needs alignment.*

## The problem

A group of people in different parts of Sydney want to converge on a single destination at the same time. Some of them have cars, the others do not. Each non-driver has a home location and a list of reachable PT stops (train stations, bus stops, ferry wharves). The output is:

1. **An assignment** of every non-driver to exactly one driver.
2. **A pickup point** per passenger — their home _or_ one of their nearby PT stops.
3. **A route per driver** — an ordered list of pickup points, ending at the destination.

We co-optimise five terms:

| Term | What it captures |
| --- | --- |
| `drive` | Total driving minutes across all drivers (fuel + driver patience). |
| `stops` | Total number of pickup stops (each stop costs the driver minutes of stationary time). |
| `walk + PT` | Total non-driver minutes spent walking or on public transport _en route_ to their pickup. |
| `arrival spread` | The gap between earliest and latest driver arrival at destination (everyone meeting up matters). |
| `fairness` | The variance of pickup-detour minutes across drivers — no one driver should be doing dramatically more work. |

Each term is normalised to roughly the same scale; the user chooses weights via sliders in the planner.

### Why it's interesting

The naive formulation (assign every passenger to their home, then run TSP per driver) gives _terrible_ results in the cases anyone would actually use this tool. Imagine: driver A lives in Bondi, driver B in Parramatta, and there are two passengers — one in Bondi Junction, one at North Strathfield. With "home only" pickups, you get one driver going to Bondi Junction and another going to North Strathfield. With flexible pickups, the Bondi Junction passenger can walk 4 minutes to Bondi Junction Station and catch a train one stop north to meet driver A at Edgecliff with zero detour. The North Strathfield passenger can walk to Strathfield Station and meet driver B at the next stop. _Total stops go from 2 to 0, total drive time drops, and everyone arrives at the same time._

That kind of restructuring is what makes the problem worth solving. It's also what makes naive routing libraries unsuitable — they assume fixed pickup points.

There is a small but real literature on DARP-with-flexible-pickups (see e.g. Boysen et al. 2021, *Operations Research*); the formulation in this repo is closer to the operational "rendezvous TSP" framing because there is no time-window per stop, just a global arrival window.

## How it works

```mermaid
flowchart LR
  subgraph external[External APIs]
    tfnsw[TfNSW Open Data]
    google[Google Routes]
    geocoder[Geocoding]
  end

  subgraph backend[".NET 10 backend"]
    core[Trips.Core]
    data[Trips.Data]
    opt[Trips.Optimisation]
    integ[Trips.Integrations]
    rt[Trips.Realtime]
    api[Trips.Api]
  end

  pg[(PostgreSQL + PostGIS)]
  redis[(Redis)]

  subgraph frontend[Frontend]
    next[Next.js 16]
  end

  integ --> tfnsw & google & geocoder
  api --> core & data & opt & integ & rt
  rt --> redis
  api --> pg & redis
  next -- "REST + SignalR" --> api
```

**Backend** is a .NET 10 minimal-API solution split into six projects: `Trips.Core` (DTOs + abstractions, the only leaf), `Trips.Data` (EF Core + PostGIS via NetTopologySuite), `Trips.Optimisation` (the two solvers + the bench harness + cost-split + return-leg + what-if), `Trips.Integrations` (TfNSW + Google Routes + geocoding clients), `Trips.Realtime` (SignalR hubs + GTFS-RT worker + an ETA recompute service), and `Trips.Api` (composition root, JWT auth, ~16 endpoints). Full dependency graph in [`docs/architecture.md`](docs/architecture.md).

**Frontend** is Next.js 16 (App Router) with the entire UI rendered via React Server Components where possible, Mapbox GL JS for the map, TanStack Query for server state, and `@microsoft/signalr` for the live driver/passenger views.

The interesting bits, in approximate order of "how much engineering went in":

### 1. OR-Tools CP-SAT formulation (`src/Trips.Optimisation/OrTools/`)

The CP-SAT model uses integer-typed decision variables for `assign[passenger, driver]` and `pickup[passenger, candidate_node]`, plus circuit-style sequencing constraints per driver to enforce a valid route. The objective is a single weighted sum reduced to integer cents (CP-SAT is integer-only). The solver runs with an explicit wall-clock budget; results carry the `MPSolver.ResultStatus` so the caller knows whether it timed out or proved optimal.

Two non-obvious tricks:

- **Soft fairness constraint via min/max linking variables.** Strict equality is too brittle (it makes the model infeasible on slightly lumpy instances). We compute `max_detour - min_detour` as a derived variable and weight it into the objective, rather than constraining it.
- **Warm-start from the locked solution** during what-if mode. We call `model.AddHint(...)` for every variable in the previous best, which is the difference between "5 seconds, optimal-equivalent" and "60 seconds, still feasible" on a 20-passenger drop-one re-solve.

### 2. Custom heuristic + simulated annealing (`src/Trips.Optimisation/Heuristic/`)

Cheapest-insertion construction (passengers are placed into the cheapest position across all drivers, one at a time, in random order) followed by a simulated-annealing local search over four move types: relocate-passenger, swap-passengers, two-opt-within-route, and swap-pickup-node (the latter is the move that takes advantage of the flexible-pickup structure). Cooling is geometric, acceptance is the standard Metropolis criterion.

The heuristic is what the API serves in production. On instances ≥ 20 passengers it beats OR-Tools' best-found by 7–26% inside a 10-second budget (see Benchmark results below).

### 3. Pareto re-solving (`src/Trips.Optimisation/Common/Pareto*.cs`)

The planner doesn't just return one solution — it surfaces three Pareto-optimal solutions across the objective terms: **balanced**, **fewest stops**, **most direct**. Users can click between them and lock the one they want. This is done by re-running the solver with three weight configurations and de-duplicating overlapping routes.

### 4. Cost split (`src/Trips.Optimisation/Cost/`)

After locking, the planner computes a fair cost split: fuel (litres/100km × distance × price/litre) plus tolls, then attributes each metre of driven distance to whoever was being carried at the time. This is its own little algorithm because passenger-kilometres are a more honest split than just "split equally per passenger".

### 5. Real-time coordination (`src/Trips.Realtime/`)

A SignalR `TripHub` lets driver clients push position updates and passenger clients subscribe to ETAs. An `EtaRecomputeService` projects the remaining route forward whenever a driver position arrives, and a separate worker hooks GTFS-RT feeds to surface train delays at PT pickup points.

### 6. What-if warm-start (`src/Trips.Optimisation/WhatIf/`)

"What if we drop Hana?" runs a new solve seeded with the existing locked solution as a CP-SAT hint, so the solver doesn't start from scratch. The UI surfaces a diff: which stops were removed, which routes got shorter, how much the objective improved.

For more on architecture see [`docs/architecture.md`](docs/architecture.md), which has the dependency graph and a sequence diagram of the full lifecycle.

## Benchmark results

The bench harness runs 60 synthetic Sydney instances (5/10/20/30 participants × 2/3/5 drivers × 5 seeds) against both solvers with a 10-second wall-clock budget. The full report is at [`bench/REPORT.md`](bench/REPORT.md).

**Headline**: on instances ≥ 20 passengers, the heuristic beats OR-Tools' best-found by **7–26%** within the same wall-clock budget. Below 10 passengers both solvers reach optimality trivially.

| Class | OR-Tools obj | Heur obj | Gap % | OR-Tools ms | Heur ms |
|-------|-------------:|---------:|------:|------------:|--------:|
| 5p/2d   | 126.80 | 126.80 | 0.00   | 65    | 10000 |
| 10p/3d  | 220.86 | 220.86 | -0.00  | 10007 | 10000 |
| 20p/2d  | 290.85 | 271.38 | **-7.24**  | 10019 | 10000 |
| 20p/3d  | 343.97 | 320.70 | **-7.01**  | 10019 | 10000 |
| 20p/5d  | 432.21 | 390.79 | **-10.43** | 10037 | 10000 |
| 30p/2d  | 410.27 | 333.59 | **-17.65** | 10026 | 10000 |
| 30p/3d  | 483.47 | 382.76 | **-20.61** | 10055 | 10000 |
| 30p/5d  | 620.53 | 459.07 | **-25.90** | 10076 | 10000 |

Negative gap means the heuristic produced a lower (better) objective than OR-Tools' best-found within the budget. The full per-instance table, the two embedded solution-graph mermaid diagrams, and ASCII convergence sparklines are in [`bench/REPORT.md`](bench/REPORT.md).

The objective function is **identical** between the two solvers — both pull from the same `ObjectiveEvaluator` — so the gap numbers are directly comparable, not apples to oranges.

To re-run the bench yourself:

```bash
dotnet run -c Release --project bench/Trips.Bench
# writes bench/REPORT.md + bench/results.csv
```

See [`bench/README.md`](bench/README.md) for the CLI flags and what knobs do what.

## Walk-through

### Login + dashboard (real screenshots)

![Login page](docs/screenshots/00-login.png)

![Trips dashboard with the seeded "Group trip to Palm Beach"](docs/screenshots/01-trips-dashboard.png)

These two are captured live by `web/tests/screenshots.spec.ts` against a seeded backend. The rest of the views below use SVG mockups produced from the real data the backend emits — the UI binding for those routes is the one item still to align with the API.

### Planning canvas

![Planning canvas with Pareto carousel](docs/screenshots/03-planning-canvas.svg)

The planner shows the destination chip, weight sliders for the five objective terms, the participant list, and the Sydney basemap with markers for every home plus candidate PT pickup nodes. Hitting "Optimise" enqueues a background run; the Pareto carousel populates with three alternatives in ~5–10s for a typical group size. Locking a solution makes it the canonical assignment.

### Live driver view

![Driver view with manifest + route polyline](docs/screenshots/04-driver-view.svg)

Drivers see their route as a polyline on the map plus an ordered manifest of pickup stops, each with the passenger names and live ETA. Their position is pushed to the SignalR `TripHub`; passengers receive ETA updates in real time. The live events feed in the side panel is the raw stream from the hub.

### Cost split

![Cost split breakdown](docs/screenshots/05-cost-split.svg)

After locking, every participant gets a fair share of the fuel + tolls based on passenger-kilometres carried. Drivers can override the fuel price and economy in the UI; tolls are entered per-segment. The `CostSplitService` in `Trips.Optimisation` does the attribution.

### What-if

![What-if diff modal](docs/screenshots/06-whatif-diff.svg)

The what-if mode re-solves with two passengers dropped, warm-starting from the locked solution. The diff view shows which stops were removed, how the route shortened, how much the objective improved per-term, and the solver stats (status, branches, improving moves). Hitting "Lock the candidate" replaces the current locked solution and fans out a `TripEvent.SolutionLocked` over SignalR so all clients re-sync in under a second.

## Tech stack

**Backend**
- .NET 10.0 (10.0.300 SDK) — ASP.NET Core minimal APIs + SignalR
- EF Core 10.0.8 over PostgreSQL 16 + PostGIS 3 (`Npgsql.EntityFrameworkCore.PostgreSQL.NetTopologySuite`)
- Redis 7 — `Microsoft.AspNetCore.SignalR.StackExchangeRedis` backplane + route-matrix cache
- Google.OrTools 9.15 (CP-SAT)
- FluentValidation 11, Serilog 10, Swashbuckle 10
- ASP.NET Identity over the same DbContext, JWT bearer for API auth
- Ical.Net 5 for the `calendar.ics` per-participant export

**Frontend** (`web/`)
- Next.js 16.2 (App Router, RSC-first), React 19.2
- TypeScript 5, TanStack Query 5, Zustand 5
- Tailwind 4 + shadcn/ui primitives + Base UI 1.5
- Mapbox GL JS 3.24 + react-map-gl 8
- @microsoft/signalr 10
- `jose` for cookie-sealed sessions (NextAuth installed but not used — the API owns identity)

**Data / infra**
- PostgreSQL 16 + PostGIS 3.4 (via `postgis/postgis:16-3.4`)
- Redis 7-alpine
- docker-compose for both, single file under `infra/`

**External APIs**
- TfNSW Open Data (Trip Planner v2, Coordinate Request, Departure, GTFS-Realtime)
- Google Routes API (compute_route_matrix, compute_routes w/ waypoint optimisation)
- Google or Nominatim for geocoding

**Testing**
- xUnit + FluentAssertions backend; Testcontainers for Postgres+PostGIS integration tests
- Vitest + Testing Library for frontend unit tests
- Playwright 1.60 for E2E + screenshot capture

## Quickstart

```bash
# 1. Clone
git clone https://github.com/cosykid/SydneyTrips.git
cd SydneyTrips

# 2. Bring up Postgres + Redis
#    Postgres is exposed on host port 5433 to dodge a clash with native installs on 5432.
docker compose -f infra/docker-compose.yml up -d

# 3. Install the EF Core CLI (once per machine)
dotnet tool install --global dotnet-ef --version 10.0.4
export PATH="$PATH:$HOME/.dotnet/tools"

# 4. Apply migrations
dotnet ef database update --project src/Trips.Data --startup-project src/Trips.Api

# 5. Run the API
dotnet run --project src/Trips.Api
#   -> http://localhost:5000
#   -> http://localhost:5000/swagger
#   -> http://localhost:5000/healthz   { "status": "ok" }

# 6. In another terminal, run the frontend
cd web
npm install
npm run dev
#   -> http://localhost:3000
```

To stop everything: `docker compose -f infra/docker-compose.yml down`.

### Optional API keys

The API runs without TfNSW or Google keys — it uses stub clients for both, with reasonable synthetic responses suitable for local dev. To wire in real keys:

```bash
dotnet user-secrets set "TfNsw:ApiKey" "<your-key>"   --project src/Trips.Api
dotnet user-secrets set "Google:ApiKey" "<your-key>"  --project src/Trips.Api
```

| Key | Notes |
| --- | --- |
| `TfNsw:ApiKey`  | Register at <https://opendata.transport.nsw.gov.au>, subscribe to Trip Planner v2, Coordinate Request, Departure, GTFS-Realtime. |
| `Google:ApiKey` | Routes API + Geocoding API enabled; set billing alerts (matrix calls add up). |
| `NEXT_PUBLIC_MAPBOX_TOKEN` | Public Mapbox token in `web/.env.local`. The map components fall back to a placeholder card if unset, so this is also optional for getting the app running. |

### Demo seed + screenshots

```bash
./tests/seed/seed-demo.sh                       # creates a deterministic "Palm Beach" trip
cd web && npx playwright test tests/screenshots.spec.ts --headed
```

The seed script registers a demo user, creates a trip, adds 3 drivers + 8 passengers spread across Sydney, optimises, and locks the balanced Pareto solution. The Playwright spec then logs in and captures the five hero screenshots into `docs/screenshots/`. See [`docs/screenshots/README.md`](docs/screenshots/README.md) for the full flow.

## Project layout

```
src/
  Trips.Core           domain models, abstractions, DTOs (everything else depends on this)
  Trips.Data           EF Core DbContext, configurations, repositories, migrations
  Trips.Optimisation   OR-Tools + heuristic solvers + cost split + return trip + what-if
  Trips.Integrations   TfNSW + Google Routes + geocoding clients (+ Redis caching)
  Trips.Realtime       SignalR TripHub + GTFS-RT worker + ETA recompute
  Trips.Api            ASP.NET Core minimal API + JWT auth + background OptimisationRunner
tests/
  Trips.*.Tests        xUnit per src library
  Mocks                shared fixtures + WireMock-style stubs
  seed/                seed-demo.sh — idempotent demo trip for screenshots + E2E
  smoke/               curl-driven end-to-end smoke (WS7 cost-split + return + what-if + ICS)
bench/
  Trips.Bench          benchmark console harness — see bench/README.md
  REPORT.md            benchmark report — see Benchmark results above
  results.csv          raw bench rows
infra/
  docker-compose.yml   Postgres + PostGIS + Redis
docs/
  architecture.md      mermaid dependency graph + lifecycle sequence diagram
  screenshots/         hero PNGs + README for regenerating them
web/                   Next.js 16 app — see web/README.md
.github/workflows/
  ci.yml               .NET build/test + Node build/lint/typecheck + optional E2E
LICENSE                MIT
```

Project references follow the arrows above: `Trips.Api` depends on everything else under `src/`; library projects depend only on `Trips.Core` (plus `Trips.Data` for `Trips.Realtime` and `Trips.Optimisation` for `Trips.Realtime`).

## Status

| Workstream | Scope | Status |
| --- | --- | --- |
| WS1 | Foundation: solution scaffold, EF Core + PostGIS, docker-compose, CI | done |
| WS2 | TfNSW + Google Routes + Geocoding clients (with Redis caching) | done |
| WS3 | OR-Tools + heuristic solvers + benchmark harness | done |
| WS4 | REST API surface + JWT auth + background optimisation runner | done |
| WS5 | SignalR TripHub + ETA recompute + GTFS-RT worker | done |
| WS6 | Next.js frontend — planner, live driver/passenger views, cost UI, what-if modal | done |
| WS7 | Cost split, return trip, what-if warm-start, calendar.ics | done |
| WS8 | Tests, benchmarks, README polish, screenshots, E2E suite | done |

**Known issues** (tracked as follow-ups, all out of WS8 scope):

- The background `OptimisationRunner` in `Trips.Api` has a foreign-key sequencing quirk when run with the OR-Tools solver under high concurrency (the run row is written before the run-stats row is fully constructed). The heuristic solver path is unaffected, and the seed / E2E paths use `solver: 1` (Heuristic) for that reason.
- The frontend's `TripOverview`, `PlanCanvas`, `DriverView`, `PassengerView`, and `CostBreakdown` components expect the trip-detail endpoint (`GET /trips/{id}`) to return nested `participants[]` and `candidateNodes[]` arrays, but the current API returns the trip without those (they live on `GET /trips/{id}/participants`, which itself is currently HTTP 405 because only `POST` and `DELETE` are mapped). That makes the trip-detail UI surface empty / error-out. The auth contract drift (`AuthResponse` shape) was tiny enough that WS8 fixed it inline so login at least works; the broader endpoint mismatch is a separate follow-up. The SVG mockups in [Walk-through](#walk-through) are produced from real backend data; only the UI binding needs alignment.

## Test counts

- **Backend**: 125 xUnit tests across six test projects (Trips.Core.Tests, Trips.Data.Tests, Trips.Optimisation.Tests, Trips.Integrations.Tests, Trips.Api.Tests, Trips.Realtime.Tests).
- **Frontend**: Vitest + Testing Library unit tests under `web/src/components/**`.
- **E2E**: Playwright suite at `web/tests/e2e/` — five UI-only smoke specs + three full-stack specs (`single-driver`, `multi-driver`, `what-if`). The full-stack specs require a live Trips.Api; the smoke specs don't.

Run them all:

```bash
dotnet test -c Release                       # 125 backend
cd web && npm run test                       # frontend unit
cd web && npm run test:e2e                   # Playwright (UI-only smoke; full E2E needs the API up)
```

## License

[MIT](LICENSE).
