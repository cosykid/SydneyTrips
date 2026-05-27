# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Common commands

### Backend (.NET 10)

```bash
# build everything
dotnet build Trips.sln -c Debug

# run all backend tests (160 across six projects)
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

# Optional OSRM (host port 5001) — serves EVERY travel-time matrix (planning + live ETA) locally,
# so Google's Route Matrix is never called. Gated behind the "routing" profile; needs a one-time
# graph build first (see docs/operations-cost.md "Running OSRM"). Then set Integrations:Osrm:BaseUrl.
docker compose -f infra/docker-compose.yml --profile routing up -d

# deterministic demo trip (opens an anonymous session, creates trip, 3 drivers + 8 passengers,
# optimises with the heuristic, locks the balanced Pareto solution)
./tests/seed/seed-demo.sh

# curl-driven end-to-end smoke test against a running API
BASE_URL=http://localhost:5000 bash tests/smoke/ws7.sh
```

**Apple Silicon (arm64):** the `postgis/postgis` and `ghcr.io/project-osrm/osrm-backend` images are
**amd64-only**, so Docker Desktop runs them under emulation and shows an orange "AMD64" badge — this
is expected, not a problem (`redis` is native arm64, no badge). The OSRM compose service pins
`platform: linux/amd64` for this reason, and the one-time graph-build commands need
`--platform linux/amd64` too (see `docs/operations-cost.md`). macOS also squats on port 5000 via
AirPlay Receiver — disable it, or run the API with `--urls http://localhost:5050` (and point
`web/.env.local`'s `API_BASE_URL`/`NEXT_PUBLIC_API_BASE_URL` at 5050).

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

**Displayed driver ETAs are shifted at map time, not as persisted.** `SolutionBuilder` stamps every `Stop.EstimatedArrival` forward from `trip.DepartAt`, which is set well before the arrival window — so raw, the driver "arrives" long before the target and idles. `Mappers.ToDto` therefore slides the whole route timeline later by one uniform `shiftMins` and emits `DriverRouteDto.Departure` + `DriverRouteDto.DestinationArrival` (and shifted per-stop `EstimatedArrival`) so the driver leaves just-in-time and lands `ArrivalSlack` (5 min) before the arrival-window midpoint. The shift is clamped `≥ 0`: a window too tight to reach on time pins to `DepartAt` (earliest-possible timing) rather than inventing a pre-`DepartAt` departure. These three fields are **computed at map time, not persisted** (and are null when a solution is mapped without its trip). For display, read the DTO — don't reach for the raw `Stop.EstimatedArrival` off the persisted entity. Pinned by `SolutionPickupPathMappingTests`.

### PostGIS / geography

Points are `NetTopologySuite.Geometries.Point` with `SRID = 4326`, **longitude first** (`new Point(lng, lat) { SRID = 4326 }`). Mapping to DTOs flattens to `{ longitude, latitude }` pairs. Don't mix the order — there's no compile-time check.

### TfNSW candidate-node generation & PT pickup legs

Passenger pickup hubs come from `ParticipantCandidateNodeService.PopulateAsync` (runs on participant-add and on `POST /trips/{id}/refresh-candidate-nodes`). It probes TfNSW (`ITfNswClient.TripPlanAsync` / `CoordinateRequestAsync`) for transit interchanges a driver can reach, planning against `trip.DepartAt − safety buffer`. Each admitted hub stores the home→hub `Path` (LineString) + mode-tagged `PathLegs`, which flow through the solution DTO to the planner map (`PlanMap.tsx`), where each walk/train/bus/ferry segment is coloured by mode.

Each `PathLeg` also carries its stop names (`FromName`/`ToName`), `DurationMins`, line label (`RouteShortName`, e.g. "T9 Northern Line", "M1 …", "389"), and scheduled `DepartureTime`/`ArrivalTime`. That's the data behind the **Google-Maps-style timed itinerary** shown when you hover a person's name in `PlanMap.tsx`: passengers get their step-by-step walk/PT breakdown (clock time per leg, line chip, durations), drivers get a pickup-by-pickup driving timeline. All those fields are optional end-to-end — stub data and `candidate_nodes` rows generated before this feature carry only mode + geometry, and the itinerary degrades to whatever's present. `path_legs` is jsonb (serialised `PathLeg` records), so adding more leg fields needs **no migration** — but does need a `refresh-candidate-nodes` to backfill existing rows.

Gotchas that have cost real debugging time:

- **A past or invalid `trip.DepartAt` silently wipes every PT hub.** EFA rejects past dates (`journeys: null`, `code -4001 "invalid date"`), so every trip-plan probe returns empty and each passenger collapses to Home-only → the solver picks everyone up by car at their doorstep (one giant detour loop). The UI symptom is "everyone driven door-to-door", or — before the empty-plan guard existed — straight crow-fly pickup lines. **Before suspecting the solver or the map, confirm `DepartAt` is in the future, then re-run `refresh-candidate-nodes`.** (A trip created days ago will have drifted into the past.)
- **An empty plan ≠ a reachable hub.** `TfNswClient.MapTripPlan` returns a *non-null* `TfNswTripPlan([], 0, 0)` when there's no journey. `TryAdmitProbedHub` skips probes totalling ≤ 0 minutes for this reason — don't relax that into admitting them, or you fabricate free, geometry-less "teleport" pickups that the map can only draw crow-fly.
- **EFA reads `itdDate`/`itdTime` as Sydney local time, but `DepartAt` is a UTC instant.** `TfNswClient` converts the departure to `Australia/Sydney` (`SydneyTimeZone`) before formatting those query params, in both `TripPlanAsync` and `DepartureAsync`. Format the raw UTC components instead and EFA plans a journey offset by the whole AEST/AEDT gap (a morning trip becomes late-night) — which then surfaces as nonsensical pickup clock times in the itinerary while the geometry still looks plausible. `TfNswClientTests.TripPlanAsync_formats_itdDate_and_itdTime_in_Sydney_local_time` pins the formatted query (the TfNSW mock now exposes `TfNswTripPlanRequests()` for this).
- **`CachingTfNswClient` must round-trip every `TfNswJourneyLeg` field**, not just geometry: `Polyline`, `FromName`/`ToName`, `DurationMins`, `RouteShortName`, and `DepartureTime`/`ArrivalTime`. It once cached only mode/duration/endpoints, so any Redis cache hit returned legs with null geometry → null `Path` → crow-fly, even on a valid date; the same failure mode now applies to the itinerary's names/times. If you add a field to `TfNswJourneyLeg`, add it to `CachedJourneyLeg` too (and assert it in `CachingDecoratorTests`).
- The planner's crow-fly dashed line is a **no-geometry fallback**, not a map bug. If you see it, inspect `candidate_nodes` first: `Path`/`path_legs` null with `WalkMins`/`PtMins` = 0 means the probe returned no journey (almost always a past `DepartAt`, or the stub client).

Diagnosing against Postgres (host port 5433): `candidate_nodes` columns are PascalCase, so quote them (`cn."Path"`, `cn."WalkMins"`); `Path` is `geometry(LineString,4326)`, `path_legs` is `jsonb`. The live TfNSW client is wired only when `Integrations:TfNsw:ApiKey` is set (user-secrets, id in `Trips.Api.csproj`); without it `StubTfNswClient` serves canned hubs with plain names ("Hornsby", "Chatswood") and **no** geometry — so stub-generated trips also render crow-fly by design.

### Realtime (Trips.Realtime)

- `TripHub` is the SignalR hub. Drivers push positions, passengers receive ETAs. There's no JWT: the hub recognises the caller via the `trips_session` cookie. The web client connects through the same Next.js proxy (`/api/proxy/hubs/trip`) using the `LongPolling` transport — Next route handlers can't tunnel WebSocket upgrades, and the cookie rides every long-poll round-trip natively. Hub authorisation goes through `ITripHubAuthorizer`.
- Redis is the SignalR backplane in production. Tests clear the `ConnectionStrings__Redis` env var (see `TripsApiFactory`'s static ctor) so the realtime layer falls back to the in-memory backplane.
- `EtaRecomputeService` walks `run history → locked solution → driver route` whenever a position update arrives, then broadcasts.

### Integration tests

`tests/Trips.Api.Tests/TripsApiFactory.cs` is the shared fixture (xUnit `ApiTests` collection — tests serialise). It:

- Spins up Postgres+PostGIS via Testcontainers (one container per fixture instance, ~10s startup).
- Applies migrations on `InitializeAsync`; `ResetAsync` TRUNCATEs the domain tables (`RESTART IDENTITY CASCADE`) between tests. (Identity/AspNet* tables are gone — auth is the anonymous `trips_session` cookie.)
- Replaces `ISolver` with `StubSolver` and the three HTTP clients with their `Stub*` siblings so tests don't hang on external retries.
- Exposes `WithRealSolver()` → sibling `WebApplicationFactory<Program>` that re-registers the real `OrToolsSolver` while keeping the HTTP stubs. Use this for end-to-end runner/solver coverage. See `RealSolverOptimisationTests` for the pattern.

`Trips.Optimisation.Tests` does **not** go through the runner — those tests exercise solvers directly. Persistence-path bugs only surface in `Trips.Api.Tests`.

## Conventions

### Commit messages

Do **not** add `Co-Authored-By: Claude …` or any AI/assistant trailer. Style: short imperative title, optional body with bullets explaining *why* the change is needed (see recent `git log` for shape). The project history uses `WSn: <thing>` for workstream commits but follow-up work doesn't need that prefix.

### Web subproject is on bleeding-edge Next.js

`web/AGENTS.md` warns: this Next.js version (16.2.6) has breaking changes relative to common training data — APIs, conventions, and file structure may differ. Read `web/node_modules/next/dist/docs/` before writing FE code. The whole UI uses App Router + RSC where possible; client components are explicit. State: TanStack Query for server state, Zustand for ephemeral client state.

### Map fallback

Maps render via Google Maps (`@vis.gl/react-google-maps`), keyed by `NEXT_PUBLIC_GOOGLE_MAPS_KEY` (plus `NEXT_PUBLIC_GOOGLE_MAPS_MAP_ID`, which defaults to `DEMO_MAP_ID`). When the key is unset — the default in CI and Playwright screenshot capture — `PlanMap`, `LiveMap`, and `MapBackdrop` render `web/src/components/map/MapFallback.tsx`, a deterministic SVG canvas drawn from real lat/lng, instead of Google Maps. Don't make new map components hard-depend on the key at runtime — gate on it and fall through to `MapFallback` the same way.

### Solver kinds in seed/smoke scripts

`tests/seed/seed-demo.sh` and `tests/smoke/ws7.sh` historically used `"solver": 1` (Heuristic) to avoid alleged FK bugs in the OR-Tools path. That folklore is stale (see "OR-Tools solver returns…" above). Either solver works end-to-end now; pick by what you want to test.
