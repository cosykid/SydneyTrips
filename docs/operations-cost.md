# Operating cost — Google Maps Platform

The one external cost that scales with usage is the **Google Routes API**, and within it the
**Route Matrix**. This note explains what drives the bill, the two root causes behind a real
~A$400 overrun, what the code now does about it, and the guardrails to set on a fresh GCP account.

## What you're billed for

Route Matrix is billed **per element**, where `elements = origins × destinations`. A single
optimisation run builds a travel-time matrix over its node-set (driver origins + passenger homes +
every candidate pickup hub + the destination), so an `n`-node run is up to `n²` elements. The SKU
also has tiers:

| SKU | Selected by | Relative price |
| --- | --- | --- |
| Compute Route Matrix **Essentials** | `routingPreference: TRAFFIC_UNAWARE` (or unset) | cheaper, larger free monthly allotment |
| Compute Route Matrix **Pro** | `routingPreference: TRAFFIC_AWARE` / `TRAFFIC_AWARE_OPTIMAL` | ~2× Essentials, smaller free allotment |

> A real incident: demo planning burned **~A$400 across two trial accounts**. Even at the
> Essentials rate, paying per element for the full `n²` matrix on every run is not sustainable at
> steady state. The fix is structural — stop paying Google per element for the *planning* matrix at
> all (see OSRM below) — not just choosing a cheaper SKU.

⚠️ Google discontinued the old flat US$200/month Maps credit (March 2025) in favour of per-SKU
monthly free tiers. **Verify the current Essentials/Pro free quotas** in *Billing → Pricing* before
assuming a given volume is free — the allotment depends on your monthly call count.

## Two root causes of the overrun

1. **The cache was never on.** `AddRedisCache` (in `DependencyInjection.cs`) decided whether caching
   was real by reading `Integrations:Cache:RedisConnectionString` — a key that *nothing* set
   (`appsettings.json` only defines `ConnectionStrings:Redis`, which the SignalR backplane uses). So
   `IIntegrationCache` resolved to `NoopIntegrationCache`: the per-pair caching decorator faithfully
   computed cache keys and handed them to a cache that always missed and never stored. **Every matrix
   call hit Google uncached, every run.** Fixed: `AddRedisCache` now falls back to
   `ConnectionStrings:Redis`, and connects with `AbortOnConnectFail=false` so a missing Redis
   degrades to "no cache" instead of crashing startup.
2. **Cold cache still pays per element.** Even with the cache on, a brand-new trip with new
   coordinates is a cold cache — you pay Google for every genuinely-new pair, and `n` is inflated by
   the multiple candidate pickup hubs per passenger. Caching blunts *repeated/overlapping* geometry;
   it cannot make a fresh trip free. That's why the planning matrix needs to leave Google entirely.

## What the code now does

1. **The cache is actually wired** (root cause #1, above). Repeated plans, what-ifs, and trips that
   share legs now reuse cached pairs instead of re-billing them.
2. **Planning uses free-flow durations.** `OptimisationRunner` calls the matrix with
   `trafficAware: false`. The planner solves against a *future* `DepartAt`, so a live-traffic snapshot
   taken at plan time is noise. Only `EtaService` (the live ETA path) passes `trafficAware: true`.
3. **Per-pair caching.** The matrix is cached one origin→destination pair at a time, keyed on
   coordinates snapped to `MatrixSnapDecimals` (4 dp ≈ 11 m) plus the traffic flag. Two trips sharing
   a leg bill it once; a re-plan that adds one node bills only that node's new row + column, not the
   whole `n²` grid. Free-flow pairs live for `RouteMatrixTtl` (**14 days**); traffic-aware pairs for
   `TrafficAwareMatrixTtl` (**1 min**). The traffic flag is part of the key, so they never collide.
4. **OSRM serves the planning matrix (the structural fix).** When `Integrations:Osrm:BaseUrl` is set,
   `HybridRoutesClient` routes the free-flow (planning) matrix to a self-hosted OSRM instance — one
   `/table` call returns the whole origins×destinations matrix locally, at **zero marginal cost** —
   and keeps Google only for the traffic-aware ETA path and the locked-solution polyline. With no
   OSRM configured it forwards everything to Google, exactly as before. **This is what makes planning
   cost ~$0 regardless of cache hit rate.**

Tunable in `Integrations:Cache` (`IntegrationCacheOptions`): `RouteMatrixTtl`,
`TrafficAwareMatrixTtl`, `MatrixSnapDecimals`.

> Note: `ComputeRoutes` (the polyline "snap" in `SolutionPostprocessor`, a separate "Compute Routes"
> SKU) still requests `TRAFFIC_AWARE`. It runs at most once per locked solution, so it is low volume.

## Running OSRM (the planning matrix, self-hosted)

The `osrm` service in `infra/docker-compose.yml` is gated behind the `routing` profile so a plain
`docker compose up` doesn't try to start it before map data exists.

**1. One-time data prep** (from `infra/`, fills `./osrm` with `region.osrm*`). Pick the smallest
extract that covers your trips — a Sydney/NSW clip beats the whole continent for build time and RAM:

```bash
mkdir -p osrm && cd osrm
curl -L -o region.osm.pbf https://download.geofabrik.de/australia-oceania/australia-latest.osm.pbf
IMG=ghcr.io/project-osrm/osrm-backend:latest
docker run --rm -v "$PWD:/data" $IMG osrm-extract  -p /opt/car.lua /data/region.osm.pbf
docker run --rm -v "$PWD:/data" $IMG osrm-partition /data/region.osrm
docker run --rm -v "$PWD:/data" $IMG osrm-customize /data/region.osrm
```

**2. Start the server:**

```bash
docker compose -f infra/docker-compose.yml --profile routing up -d osrm
```

**3. Point the API at it** — set `Integrations:Osrm:BaseUrl` to `http://localhost:5001` (user-secret
or appsettings). On the next optimisation run the log shows `travel matrix snapped to Google driving
times` is now served by OSRM; the Routes API dashboard should show planning traffic drop to ~zero,
leaving only the live-ETA calls.

If OSRM is configured but unavailable, the planning matrix call fails and
`OptimisationRunner.EnrichWithDrivingMatrixAsync` keeps the haversine (crow-fly) estimate for that
run — a bounded, free degradation. It is **not** silently re-routed to Google, by design: that would
re-introduce exactly the surprise cost we're removing.

## Which Google APIs the app uses (and which key)

There are **two** API keys with different scopes. Enable the APIs at the project level, then restrict
each key to only the subset it actually calls.

**Backend key — `Integrations:Google:ApiKey`** (server-side; restrict by IP):

| API | Called by | Needed? |
| --- | --- | --- |
| **Routes API** | `GoogleRoutesClient` — `computeRouteMatrix` (planning) + `computeRoutes` (locked-solution polyline) | Required |
| **Geocoding API** | `GoogleGeocodingClient` (`maps/api/geocode/json`) | Only if `Integrations:Geocoding:Provider=google`; the default is Nominatim, so usually **not** |

**Frontend key — `NEXT_PUBLIC_GOOGLE_MAPS_KEY`** (browser; restrict by HTTP referrer):

| API | Called by | Needed? |
| --- | --- | --- |
| **Maps JavaScript API** | `PlanMap` / `LiveMap` / `MapBackdrop` vector maps | For real maps |
| **Places API (New)** | `PlaceAutocomplete` — address search in the create-trip + participant forms | For address autocomplete |
| **Routes API** | `useRoutePolylines` → `Route.computeRoutes` client-side, for road-snapped driver paths | For snapped polylines |

The frontend also needs a **Map ID** (Console → Map Management) for the vector maps; the code defaults
to `"DEMO_MAP_ID"` for dev — create a real vector Map ID (`NEXT_PUBLIC_GOOGLE_MAPS_MAP_ID`) for
production.

> The ~A$400 incident was **only** the backend Route Matrix — the runaway this doc's code changes
> target. The frontend key has its own, smaller spend the OSRM/cache work does **not** touch (it is
> browser-side): dynamic Maps JS loads, Places Autocomplete (billed per session/request — adds up with
> heavy typing), and client-side `computeRoutes` (one call per driver route per plan render, cached by
> a route hash so re-renders are cheap). Cap the frontend key's per-API quotas too, not just the
> backend Route Matrix. Both frontend features degrade gracefully without a key (maps → the SVG
> `MapFallback`; autocomplete → a plain text input), so the app still runs.
>
> Follow-up worth doing: the backend already computes routes in `SolutionPostprocessor`, and the
> frontend calls `computeRoutes` again for the same polylines — serving the backend geometry to the
> frontend would drop the client-side Routes calls entirely.

## Setting up a fresh GCP account (do these before wiring the key)

After a trial account is exhausted you cannot un-exhaust it; a new account starts a new free trial.
Before you put the new key anywhere the app can call it, set the guardrails — otherwise a cold cache
or a runaway loop spends the new trial the same way.

1. **Enable only the APIs each key uses** — see *Which Google APIs the app uses* above. Backend key:
   Routes API (+ Geocoding API only if you use Google geocoding). Frontend key adds Maps JavaScript
   API and Places API (New). Nothing beyond what each key calls.
2. **Restrict each key** (Credentials → the key): *API restrictions* to just that key's subset, plus
   an *application restriction* — IP for the backend key, HTTP referrer for the browser key — so a
   leaked key can't be used elsewhere.
3. **Budget alert** (Billing → Budgets & alerts) — see below. Notifies; does not stop spend.
4. **Hard quota cap** on the Route Matrix element/request quotas — see below. This is the actual
   spend ceiling.
5. **Stand up OSRM and set `Integrations:Osrm:BaseUrl`** so planning never touches Google. With OSRM
   on, the new account should only ever see low-volume live-ETA + polyline calls.
6. Only then put the new key in `Integrations:Google:ApiKey` (user-secret).

### Budget alert — get told before it hurts

Console: **Billing → Budgets & alerts → Create budget**, scope to the Maps Platform / Routes
service, set thresholds (e.g. 50% / 90% / 100% of your monthly ceiling) with email notifications.

Or via gcloud (replace the billing-account and amount):

```bash
gcloud billing budgets create \
  --billing-account=XXXXXX-XXXXXX-XXXXXX \
  --display-name="Routes API monthly" \
  --budget-amount=20AUD \
  --threshold-rule=percent=0.5 \
  --threshold-rule=percent=0.9 \
  --threshold-rule=percent=1.0
```

A budget alert **notifies**; it does not stop spend. That's what the quota cap is for.

### Hard quota cap — make runaway spend impossible

Console: **APIs & Services → Routes API → Quotas & System Limits**, filter to the Route Matrix
element/request quotas, and set a **per-day limit** that comfortably covers real usage but not a
runaway loop. When the cap is hit, matrix calls return `RESOURCE_EXHAUSTED` and the app degrades
gracefully — `OptimisationRunner.EnrichWithDrivingMatrixAsync` catches the failure and keeps the
haversine (crow-fly) estimate for that run rather than failing it. Set the cap high enough that this
fallback is a true emergency, not a daily occurrence.
