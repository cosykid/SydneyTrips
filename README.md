# SydneyTrips

SydneyTrips is a group-trip planner for Sydney. It helps a group travelling to one destination decide who drives, who rides with whom, where passengers should meet their driver, and when everyone should leave.

The core problem is more than routing cars between fixed addresses. Each passenger can be picked up at home or at a reachable public-transport stop, so the planner can trade a short train or walk leg for less driving, fewer stops, tighter arrival times, and a fairer driver burden.

## What It Does

- Creates anonymous, shareable trips with no sign-in flow.
- Adds drivers and passengers with home locations and candidate pickup points.
- Optimises assignments, pickup points, and driver stop order.
- Returns multiple plan options so the group can choose between balanced, fewer-stop, and more-direct trips.
- Locks a solution for the live trip views.
- Shows driver and passenger views with SignalR-powered ETA updates.
- Computes fuel and toll cost splits from the locked route.
- Supports what-if re-planning, return trips, and per-participant calendar exports.

## How It Works

The backend is a .NET 10 minimal API split into small projects:

```text
src/
  Trips.Core           Domain models, DTOs, abstractions
  Trips.Data           EF Core, PostgreSQL, PostGIS, repositories, migrations
  Trips.Optimisation   OR-Tools solver, heuristic solver, cost split, what-if
  Trips.Integrations   TfNSW, Google Routes, geocoding, OSRM, Redis caching
  Trips.Realtime       SignalR hub, GTFS-RT worker, ETA recompute
  Trips.Api            HTTP endpoints, anonymous sessions, background jobs
```

The frontend lives in `web/` and is a Next.js 16 app using React 19, TypeScript, TanStack Query, Tailwind, shadcn/ui primitives, Google Maps, and SignalR.

Planning runs through a background optimisation job:

1. The browser creates a trip and adds participants.
2. The API builds candidate pickup nodes for each passenger.
3. An optimisation run scores driver/passenger assignments, pickup nodes, and route order.
4. The frontend polls for the resulting plan options.
5. A selected solution is locked and reused by driver, passenger, cost, calendar, and what-if flows.

See [docs/architecture.md](docs/architecture.md) for the full dependency graph and lifecycle sequence.

## Optimisation

SydneyTrips includes two solver paths:

- `Trips.Optimisation/OrTools`: a CP-SAT formulation using Google OR-Tools.
- `Trips.Optimisation/Heuristic`: a cheapest-insertion construction followed by simulated annealing.

Both evaluate plans with the same objective terms:

- total driving time
- number of pickup stops
- passenger public-transport access time
- spread between driver arrival times
- driver fairness / detour burden

The benchmark harness in `bench/Trips.Bench` compares solver behaviour across synthetic Sydney instances. The generated report is in [bench/REPORT.md](bench/REPORT.md).

## Quickstart

Prerequisites:

- .NET 10 SDK
- Node.js and npm
- Docker Desktop or compatible Docker runtime

Start Postgres, PostGIS, and Redis:

```bash
docker compose -f infra/docker-compose.yml up -d
```

Apply database migrations:

```bash
dotnet tool install --global dotnet-ef --version 10.0.4
export PATH="$PATH:$HOME/.dotnet/tools"
dotnet ef database update --project src/Trips.Data --startup-project src/Trips.Api
```

Run the API:

```bash
dotnet run --project src/Trips.Api
```

The API listens at:

- `http://localhost:5000`
- `http://localhost:5000/swagger`
- `http://localhost:5000/healthz`

Run the frontend:

```bash
cd web
npm install
npm run dev
```

The web app listens at `http://localhost:3000`.

To stop local infrastructure:

```bash
docker compose -f infra/docker-compose.yml down
```

## Configuration

The app runs locally without TfNSW or Google keys. Missing integrations fall back to stub clients where practical, which is enough for local development and tests.

Optional backend secrets:

```bash
dotnet user-secrets set "Integrations:TfNsw:ApiKey" "<your-key>" --project src/Trips.Api
dotnet user-secrets set "Integrations:Google:ApiKey" "<your-key>" --project src/Trips.Api
```

Equivalent environment variables:

```bash
Integrations__TfNsw__ApiKey=<your-key>
Integrations__Google__ApiKey=<your-key>
```

Optional frontend environment in `web/.env.local`:

```bash
NEXT_PUBLIC_API_BASE_URL=http://localhost:5000
API_BASE_URL=http://localhost:5000
NEXT_PUBLIC_GOOGLE_MAPS_KEY=<browser-key>
NEXT_PUBLIC_GOOGLE_MAPS_MAP_ID=<map-id>
```

Google Route Matrix can become expensive because it is billed by origin-destination element. The repo supports Redis pair caching and optional self-hosted OSRM for local travel-time matrices. Read [docs/operations-cost.md](docs/operations-cost.md) before wiring real Google keys.

## Useful Commands

Backend:

```bash
dotnet build
dotnet test
dotnet run --project src/Trips.Api
dotnet run -c Release --project bench/Trips.Bench
```

Frontend:

```bash
cd web
npm run dev
npm run build
npm run lint
npm run typecheck
npm run test
npm run test:e2e
```

## Project Layout

```text
bench/                  Benchmark harness and generated report
docs/                   Architecture and operating-cost notes
infra/                  Docker Compose for Postgres, PostGIS, Redis, optional OSRM
src/                    .NET backend projects
tests/                  xUnit, integration, realtime, and smoke tests
web/                    Next.js frontend
.github/workflows/      CI
```

## License

[MIT](LICENSE)
