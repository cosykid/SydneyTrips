# SydneyTrips

A group-trip coordinator for Sydney: many origins, one destination, mixed drivers/passengers, public-transport rendezvous points. Optimises the assignment of riders to drivers and the choice of pickup nodes (homes vs. PT hubs) against a multi-objective cost — driving time, number of stops, passenger walk/PT time, and arrival-spread.

The interesting problem is a **Dial-a-Ride Problem with flexible pickup points**. Every passenger has a *set* of feasible pickup nodes — their home plus reachable train stations / bus stops — and the solver co-optimises driver routing with rendezvous selection.

## Stack

- **.NET 9** ASP.NET Core Web API + SignalR
- **PostgreSQL 16 + PostGIS** via EF Core + NetTopologySuite
- **Redis** for route/matrix cache + SignalR backplane
- **Google.OrTools** (CP-SAT) + a custom heuristic solver, with a benchmark harness comparing them
- **Next.js 15** + TypeScript + Tailwind + shadcn/ui + Mapbox GL JS
- **TfNSW Open Data** (Trip Planner v2, Coordinate Request, Departure, GTFS-RT)
- **Google Routes API** (compute_route_matrix, compute_routes w/ waypoint optimisation)

## Status

🚧 Under construction. See [plan](../.claude/plans/me-i-want-to-abstract-dragonfly.md) for the workstream breakdown.

## Quickstart

> Will fill this in once WS1 lands.

```bash
docker compose -f infra/docker-compose.yml up -d
dotnet ef database update --project src/Trips.Data
dotnet run --project src/Trips.Api
cd web && npm run dev
```
