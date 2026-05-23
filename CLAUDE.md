# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Common commands

### Backend (.NET 10)

```bash
# build everything
dotnet build Trips.sln -c Debug

# run all backend tests (125 across six projects)
dotnet test Trips.sln -c Debug

# run a single test project
dotnet test tests/Trips.Api.Tests/Trips.Api.Tests.csproj -c Debug

# run a single test by name
dotnet test tests/Trips.Api.Tests/Trips.Api.Tests.csproj --filter "FullyQualifiedName~OptimisationEndpointsTests.GetRun_polls_until_completed"

# run the API (default port 5000; pass --urls http://localhost:5050 to override)
dotnet run --project src/Trips.Api

# bench harness — regenerates bench/REPORT.md + bench/results.csv
dotnet run -c Release --project bench/Trips.Bench
```

### EF Core migrations

```bash
# install CLI once per machine
dotnet tool install --global dotnet-ef --version 10.0.4

# apply
dotnet ef database update --project src/Trips.Data --startup-project src/Trips.Api

# add a new one — the DbContext lives in Trips.Data; the startup project is the API
dotnet ef migrations add <Name> --project src/Trips.Data --startup-project src/Trips.Api
```

### Frontend (Next.js 16, App Router)

```bash
cd web
npm run dev               # localhost:3000
npm run lint              # eslint
npm run typecheck         # tsc --noEmit
npm run test              # vitest unit tests
npm run test:e2e          # Playwright; full-stack specs need the API up
npm run gen:api           # regenerates web/src/lib/api/types.ts from src/Trips.Api/openapi.json
```

When backend contracts change (DTOs / endpoints), regenerate `src/Trips.Api/openapi.json` by running the API once and capturing `/openapi/v1.json`, then run `npm run gen:api` so the FE types follow.

### Infra

```bash
# Postgres+PostGIS on host port 5433 (NOT 5432, to dodge native installs) + Redis on 6379
docker compose -f infra/docker-compose.yml up -d
docker compose -f infra/docker-compose.yml down

# deterministic demo trip (registers a user, creates trip, 3 drivers + 8 passengers,
# optimises with the heuristic, locks the balanced Pareto solution)
./tests/seed/seed-demo.sh

# curl-driven end-to-end smoke test against a running API
BASE_URL=http://localhost:5000 bash tests/smoke/ws7.sh
```

## Architecture

The README and `docs/architecture.md` cover the user-facing pitch and the dependency graph. The points below are the ones that aren't obvious from a single file.

### Backend composition root

`src/Trips.Api/Program.cs` is the only project that references all the others. The DI ordering inside it matters:

1. `AddTripsIntegrations` and `AddTripsOptimisation` register the real `ISolver` (OrTools by default), `ITfNswClient`, `IGoogleRoutesClient`, `IGeocodingClient`.
2. The block below them uses `TryAdd` / `services.Any(d => d.ServiceType == typeof(ISolver))` to register stub fallbacks **only when** the real registration didn't land. This is what makes the API survive without API keys.
3. The `OptimisationJobQueue` (singleton, `Channel<OptimisationJob>`-backed) and `OptimisationRunner` (`IHostedService`, bounded by `Optimisation:MaxConcurrent`) handle async solver work — endpoints enqueue, the runner consumes.

### The OR-Tools solver returns a fully-built Solution graph with preset keys

`SolutionBuilder.Build` (in `Trips.Optimisation/Common/`) stamps fresh `Guid`s on every `Solution` / `DriverRoute` / `Stop` before they reach EF. Two implications:

- `OptimisationRunner.ExecuteJobAsync` calls **both** `run.MarkCompleted(solution, …)` (which adds to the tracked nav collection) and `db.Solutions.Add(solution)`. The explicit `Add` is **load-bearing** — EF Core treats entities discovered through a nav collection with non-default keys as `Unchanged`, which would mean a phantom UPDATE on SaveChanges. The explicit `Add` forces `EntityState.Added`. EF's change tracker dedupes the same instance, so this is **not** a double insert. (Confirmed by `RealSolverOptimisationTests` and `tests/smoke/ws7.sh` with `solver: 0`.)
- Old folklore about an "FK quirk" or needing `solver: 1` (Heuristic) in smoke flows is stale.

### Solver selection

`OptimisationRunner.ResolveSolver` reads `SolverKind` off the job and picks from `IEnumerable<ISolver>`. Both `OrToolsSolver` and `HeuristicSolver` are registered; the keyed-DI registrations (`"or-tools"`, `"heuristic"`) exist so callers can pick deliberately. `OptimiseRequest.Solver` defaults to `SolverKind.OrTools` (= 0).

### Trip-detail endpoint shape

`GET /trips/{id}` eager-loads `participants[]` (and per-participant `candidateNodes[]`) via `TripRepository.GetWithParticipantsAsync`. The FE's `TripOverview`, `PlanCanvas`, `DriverView`, and `CostBreakdown` all read this in one round-trip — don't add separate fetches for participants. The corresponding shape lives in `src/Trips.Core/Contracts/TripDtos.cs` + `ParticipantDtos.cs`; FE adapters in `web/src/lib/api/adapters.ts` translate to UI types.

### PostGIS / geography

Points are `NetTopologySuite.Geometries.Point` with `SRID = 4326`, **longitude first** (`new Point(lng, lat) { SRID = 4326 }`). Mapping to DTOs flattens to `{ longitude, latitude }` pairs. Don't mix the order — there's no compile-time check.

### Realtime (Trips.Realtime)

- `TripHub` is the SignalR hub. Drivers push positions, passengers receive ETAs. JWT auth flows via the `access_token` query string (handled in `Program.cs:JwtBearerEvents.OnMessageReceived`) because WebSocket upgrades can't carry the `Authorization` header.
- Redis is the SignalR backplane in production. Tests clear the `ConnectionStrings__Redis` env var (see `TripsApiFactory`'s static ctor) so the realtime layer falls back to the in-memory backplane.
- `EtaRecomputeService` walks `run history → locked solution → driver route` whenever a position update arrives, then broadcasts.

### Integration tests

`tests/Trips.Api.Tests/TripsApiFactory.cs` is the shared fixture (xUnit `ApiTests` collection — tests serialise). It:

- Spins up Postgres+PostGIS via Testcontainers (one container per fixture instance, ~10s startup).
- Applies migrations on `InitializeAsync`; `ResetAsync` TRUNCATEs domain + identity tables between tests.
- Replaces `ISolver` with `StubSolver` and the three HTTP clients with their `Stub*` siblings so tests don't hang on external retries.
- Exposes `WithRealSolver()` → sibling `WebApplicationFactory<Program>` that re-registers the real `OrToolsSolver` while keeping the HTTP stubs. Use this for end-to-end runner/solver coverage. See `RealSolverOptimisationTests` for the pattern.

`Trips.Optimisation.Tests` does **not** go through the runner — those tests exercise solvers directly. Persistence-path bugs only surface in `Trips.Api.Tests`.

## Conventions

### Commit messages

Do **not** add `Co-Authored-By: Claude …` or any AI/assistant trailer. Style: short imperative title, optional body with bullets explaining *why* the change is needed (see recent `git log` for shape). The project history uses `WSn: <thing>` for workstream commits but follow-up work doesn't need that prefix.

### Web subproject is on bleeding-edge Next.js

`web/AGENTS.md` warns: this Next.js version (16.2.6) has breaking changes relative to common training data — APIs, conventions, and file structure may differ. Read `web/node_modules/next/dist/docs/` before writing FE code. The whole UI uses App Router + RSC where possible; client components are explicit. State: TanStack Query for server state, Zustand for ephemeral client state.

### Map fallback

`web/src/components/map/MapFallback.tsx` is a deterministic SVG canvas drawn from real lat/lng. When `NEXT_PUBLIC_MAPBOX_TOKEN` is unset (the default in CI and Playwright screenshot capture), `PlanMap` and `LiveMap` render this instead of Mapbox. Don't make new map components depend on a Mapbox token at runtime — go through the same fallback.

### Solver kinds in seed/smoke scripts

`tests/seed/seed-demo.sh` and `tests/smoke/ws7.sh` historically used `"solver": 1` (Heuristic) to avoid alleged FK bugs in the OR-Tools path. That folklore is stale (see "OR-Tools solver returns…" above). Either solver works end-to-end now; pick by what you want to test.
