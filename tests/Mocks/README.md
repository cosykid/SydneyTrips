# Trips.Mocks

WireMock.Net-backed fixture servers that mimic the live integration surfaces consumed by `Trips.Integrations`:

- **TfNSW Open Data** — `/v1/tp/trip`, `/v1/tp/coord`, `/v1/tp/departure_mon`, `/v1/gtfs/realtime/*`
- **Google Routes / Geocoding** — `/distanceMatrix/v2:computeRouteMatrix`, `/directions/v2:computeRoutes`, `/maps/api/geocode/json`
- **Nominatim** — `/search`, `/reverse`

Two ways to consume it:

## In-process for tests

Reference the project, resolve an `IFixtureServerFactory`, call `Create()`. Each `IMockServerSet` boots a WireMock instance per surface on a dynamic port. See `tests/Trips.Integrations.Tests` for examples.

```csharp
using Trips.Mocks;

var factory = new FixtureServerFactory();
using var servers = factory.Create();
// servers.TfNswBaseUrl, servers.GoogleBaseUrl, servers.NominatimBaseUrl
```

## Standalone for the frontend / manual exploration

Boots all three mocks on fixed ports so a web client can talk to them without API keys.

```bash
dotnet run --project tests/Mocks -- start
```

| Surface | Port |
| ------- | ---- |
| TfNSW | `http://localhost:3001` |
| Google Routes + Geocoding | `http://localhost:3002` |
| Nominatim | `http://localhost:3003` |

Smoke test:

```bash
curl -s "http://localhost:3001/v1/tp/trip?name_origin=151.2073:-33.873:EPSG:4326"
curl -sX POST http://localhost:3002/distanceMatrix/v2:computeRouteMatrix -d '{}'
curl -s "http://localhost:3003/search?q=bondi"
```

Press `Ctrl+C` (or `kill <pid>`) to stop.

## Fixtures

JSON fixtures live under `Fixtures/` and are copied to the build output as `Fixtures/...`. The TfNSW mock chooses the right trip-plan / coordinate fixture based on origin/destination coordinates — for example, an origin near `151.2073` (Town Hall) routes to `tfnsw/trip-cbd-to-bondi.json`.

The GTFS-Realtime feed is synthesised at startup via `Google.Protobuf` so we don't commit a binary fixture.
