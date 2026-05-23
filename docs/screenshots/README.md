# Screenshots

Source images for the top-level `README.md` and the architecture notes.

## Files

| File | Type | What it shows |
| ---- | ---- | ------------- |
| `00-login.png`            | real | Sign-in page (unauthenticated). |
| `01-trips-dashboard.png`  | real | Trips dashboard after logging in as the seeded `demo@sydneytrips.dev` user, with the seeded "Group trip to Palm Beach" card visible. |
| `02-trip-overview.png`    | real | Trip overview — locked-solution badge, calendar-hold buttons per participant, and the full participant list rendered from the eager-loaded `participants[]` on `GET /trips/{id}`. |
| `03-planning-canvas.png`  | real | Planner — weight sliders for the five objective terms, optimise button, participant + candidate-node markers on the map, trip name chip. |
| `04-driver-view.png`      | real | Driver manifest — ordered pickup stops with ETAs, Google/Apple Maps deep links, live SignalR connection badge, route polyline through the picks to the destination. |
| `05-cost-split.png`       | real | Cost-split breakdown — one card per participant with fuel + tolls + total, plus the `driver pays nothing` callout when applicable. |
| `06-whatif-diff.svg`      | mockup | What-if dialog: scenario inputs on the left, diff on the right. Capture spec lives in `web/tests/screenshots.spec.ts`; needs a planner session with the locked solution selected in the carousel (see [Regenerating](#regenerating-the-screenshots)). |

All real PNGs are produced by `web/tests/screenshots.spec.ts` against a backend seeded by `tests/seed/seed-demo.sh`. The map area in the planner / driver screenshots is rendered by a token-free SVG fallback canvas (`web/src/components/map/MapFallback.tsx`) when `NEXT_PUBLIC_MAPBOX_TOKEN` is unset — the origins, candidate nodes, destination star, and locked route are still drawn from real backend coordinates, just without the Mapbox basemap.

## Regenerating the screenshots

```bash
# 1. Bring up the stack
docker compose -f infra/docker-compose.yml up -d
dotnet ef database update --project src/Trips.Data --startup-project src/Trips.Api

# 2. Run the API in one terminal
dotnet run --project src/Trips.Api

# 3. Run the Next.js dev server in another terminal — make sure web/.env.local
#    has at least AUTH_SECRET=<32+ chars> and API_BASE_URL=http://localhost:5000.
#    Optionally set NEXT_PUBLIC_MAPBOX_TOKEN for a full Mapbox basemap.
cd web && npm run dev

# 4. Seed the deterministic demo trip
./tests/seed/seed-demo.sh                       # writes /tmp/seed-demo.json

# 5. Capture screenshots
cd web && npx playwright test tests/screenshots.spec.ts --headed
```

The spec reads `/tmp/seed-demo.json`, logs in, navigates to each page, and writes the PNGs back into this folder. Step 5 produces `00-login.png` through `06-whatif-diff.png`.

## Mapbox token

If `NEXT_PUBLIC_MAPBOX_TOKEN` is unset in `web/.env.local`, the map components fall back to a deterministic SVG canvas that still positions every origin / pickup / destination / route based on the trip's actual lat/lng data. It's not a basemap — there's no Sydney coastline behind the dots — but it's real product UI built from real data. Set the token to get full-fidelity captures:

```bash
echo "NEXT_PUBLIC_MAPBOX_TOKEN=pk.<your-public-mapbox-token>" >> web/.env.local
```

## Why these seven

These are the screenshots referenced from the top-level README:

- **Login + dashboard** are the entry points.
- **Trip overview** is the home page for one trip — participants, calendar holds, lock status.
- **Planning + Pareto** is the interactive heart of the product.
- **Driver view** is the moment-of-truth output — what someone actually uses on the road.
- **Cost split** is a small but distinctive feature that shows the depth of follow-through past "we solved the routing problem".
- **What-if diff** demonstrates the CP-SAT warm-start re-solve, which is the most technically interesting feature on the backend after the solver itself.
