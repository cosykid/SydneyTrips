# Screenshots

Source images for the top-level `README.md` and the architecture notes.

## Files

| File | Type | What it shows |
| ---- | ---- | ------------- |
| `01-trips-dashboard.png`  | real | Trips dashboard (the entry point — no login), with the seeded "Group trip to Palm Beach" card visible. |
| `02-trip-overview.png`    | real | Trip overview — locked-solution badge, calendar-hold buttons per participant, and the full participant list rendered from the eager-loaded `participants[]` on `GET /trips/{id}`. |
| `03-planning-canvas.png`  | real | Planner — route-priority sliders, optimise button, participant + candidate-node markers on the map, trip name chip. |
| `04-driver-view.png`      | real | Driver manifest — ordered pickup stops with ETAs, Google/Apple Maps deep links, live SignalR connection badge, route polyline through the picks to the destination. |
| `05-cost-split.png`       | real | Cost-split breakdown — one card per participant with fuel + tolls + total, plus the `driver pays nothing` callout when applicable. |
| `06-whatif-diff.svg`      | mockup | What-if dialog: scenario inputs on the left, diff on the right. Capture spec lives in `web/tests/screenshots.spec.ts`; needs a planner session with the locked solution selected in the carousel (see [Regenerating](#regenerating-the-screenshots)). |

All real PNGs are produced by `web/tests/screenshots.spec.ts` against a backend seeded by `tests/seed/seed-demo.sh`. The map area in the planner / driver screenshots is rendered by a key-free SVG fallback canvas (`web/src/components/map/MapFallback.tsx`) when `NEXT_PUBLIC_GOOGLE_MAPS_KEY` is unset — the origins, candidate nodes, destination star, and locked route are still drawn from real backend coordinates, just without the Google Maps basemap.

## Regenerating the screenshots

```bash
# 1. Bring up the stack
docker compose -f infra/docker-compose.yml up -d
dotnet ef database update --project src/Trips.Data --startup-project src/Trips.Api

# 2. Run the API in one terminal
dotnet run --project src/Trips.Api

# 3. Run the Next.js dev server in another terminal — make sure web/.env.local
#    has at least API_BASE_URL=http://localhost:5000 (and NEXT_PUBLIC_API_BASE_URL).
#    Optionally set NEXT_PUBLIC_GOOGLE_MAPS_KEY for a full Google Maps basemap.
cd web && npm run dev

# 4. Seed the deterministic demo trip
./tests/seed/seed-demo.sh                       # writes /tmp/seed-demo.json

# 5. Capture screenshots
cd web && npx playwright test tests/screenshots.spec.ts --headed
```

The spec reads `/tmp/seed-demo.json`, loads the seeded `trips_session` cookie into the browser, navigates to each page, and writes the PNGs back into this folder. Step 5 produces `01-trips-dashboard.png` through `06-whatif-diff.png`.

## Google Maps key

If `NEXT_PUBLIC_GOOGLE_MAPS_KEY` is unset in `web/.env.local`, the map components fall back to a deterministic SVG canvas that still positions every origin / pickup / destination / route based on the trip's actual lat/lng data. It's not a basemap — there's no Sydney coastline behind the dots — but it's real product UI built from real data. Set the key to get full-fidelity captures:

```bash
echo "NEXT_PUBLIC_GOOGLE_MAPS_KEY=<your-browser-maps-key>" >> web/.env.local
```

## Why these six

These are the screenshots referenced from the top-level README:

- **Dashboard** is the entry point (no login step).
- **Trip overview** is the home page for one trip — participants, calendar holds, lock status.
- **Planning + Pareto** is the interactive heart of the product.
- **Driver view** is the moment-of-truth output — what someone actually uses on the road.
- **Cost split** is a small but distinctive feature that shows the depth of follow-through past "we solved the routing problem".
- **What-if diff** demonstrates the CP-SAT warm-start re-solve, which is the most technically interesting feature on the backend after the solver itself.
