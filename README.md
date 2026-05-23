# SydneyTrips

A group-trip coordinator for Sydney: many origins, one destination, mixed drivers/passengers, public-transport rendezvous points. Optimises the assignment of riders to drivers and the choice of pickup nodes (homes vs. PT hubs) against a multi-objective cost - driving time, number of stops, passenger walk/PT time, and arrival-spread.

The interesting problem is a **Dial-a-Ride Problem with flexible pickup points**. Every passenger has a *set* of feasible pickup nodes - their home plus reachable train stations / bus stops - and the solver co-optimises driver routing with rendezvous selection.

## Stack

- **.NET 10** ASP.NET Core Web API + SignalR
- **PostgreSQL 16 + PostGIS** via EF Core + NetTopologySuite
- **Redis** for route/matrix cache + SignalR backplane
- **Google.OrTools** (CP-SAT) + a custom heuristic solver, with a benchmark harness comparing them
- **Next.js 15** + TypeScript + Tailwind + shadcn/ui + Mapbox GL JS
- **TfNSW Open Data** (Trip Planner v2, Coordinate Request, Departure, GTFS-RT)
- **Google Routes API** (compute_route_matrix, compute_routes w/ waypoint optimisation)

## Workstream status

| Workstream | Scope | Status |
| --- | --- | --- |
| WS1 | Foundation: solution scaffold, EF Core + PostGIS, docker-compose, CI | done |
| WS2 | TfNSW + Google Routes + Geocoding clients | not started |
| WS3 | OR-Tools + heuristic solvers + benchmark harness | not started |
| WS4 | REST API surface + background optimisation runner | not started |
| WS5 | SignalR realtime + GTFS-RT worker | not started |
| WS6 | Next.js frontend | not started |
| WS7 | Cost split, return-trip, what-if | not started |
| WS8 | Tests, benchmarks, docs | not started |

Plan source-of-truth: `~/.claude/plans/me-i-want-to-abstract-dragonfly.md` (local to the author).

## Prerequisites

- **.NET SDK 10.0.x** ([install](https://dotnet.microsoft.com/download))
- **Docker Desktop** running locally
- **Node 20+ / npm 10+** (only needed once WS6 lands)
- A **TfNSW Open Data** account with API key subscribed to: Trip Planner v2, Coordinate Request, Departure, GTFS-Realtime - register at <https://opendata.transport.nsw.gov.au>
- A **Google Maps Platform** key with Routes API + Geocoding API enabled - <https://console.cloud.google.com/google/maps-apis>

The TfNSW + Google keys are only needed once WS2 lands. WS1 runs without them.

## Quickstart

```bash
# 1. Clone
git clone https://github.com/cosykid/SydneyTrips.git
cd SydneyTrips

# 2. Bring up Postgres + Redis
#    Postgres is exposed on host port 5433 (so it doesn't clash with a native install on 5432).
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
#   -> http://localhost:5000/healthz   # { "status": "ok" }
```

To stop everything: `docker compose -f infra/docker-compose.yml down`.

## Secrets

API keys are stored via `dotnet user-secrets` so they never end up in source control. From the repo root:

```bash
dotnet user-secrets set "TfNsw:ApiKey" "<your-key>"   --project src/Trips.Api
dotnet user-secrets set "Google:ApiKey" "<your-key>"  --project src/Trips.Api
```

The full set of expected keys:

| Key | Required from | Notes |
| --- | --- | --- |
| `TfNsw:ApiKey`  | WS2 | TfNSW Open Data; needs four product subscriptions (see Prereqs) |
| `Google:ApiKey` | WS2 | Routes API + Geocoding API enabled, billing alerts recommended |

Defaults for non-secret config live in `src/Trips.Api/appsettings.json` (connection strings) and `appsettings.Development.json` (verbose logging).

## Solution layout

```
src/
  Trips.Api             ASP.NET Core Web API + SignalR host
  Trips.Core            domain models, abstractions, DTOs
  Trips.Data            EF Core DbContext, configurations, repositories, migrations
  Trips.Optimisation    OR-Tools + heuristic solvers + benchmark harness   (WS3)
  Trips.Integrations    TfNSW + Google Routes + Geocoding clients          (WS2)
  Trips.Realtime        SignalR hubs + GTFS-RT workers                     (WS5)
tests/
  Trips.*.Tests         xUnit per src library
bench/
  Trips.Bench           benchmark console harness                          (WS3)
infra/
  docker-compose.yml    Postgres+PostGIS, Redis
web/                    Next.js app                                        (WS6)
```

Project references follow the dependency arrows in the plan: `Trips.Api` depends on everything else under `src/`; library projects depend only on `Trips.Core` (plus `Trips.Data` for `Trips.Realtime`).

## Verification

The full WS1 verification command sequence (`dotnet restore` -> `dotnet build` -> `dotnet test` -> `docker compose up` -> `dotnet ef database update` -> `dotnet run` -> `curl /healthz` -> `docker compose down`) is documented in this README's Quickstart. Subsequent workstreams add their own.

## License

TBD.
