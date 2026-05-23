# Screenshots

Source images for the top-level `README.md` and the architecture notes.

## Files

| File | Type | What it shows |
| ---- | ---- | ------------- |
| `00-login.png`            | real | Sign-in page (unauthenticated). |
| `01-trips-dashboard.png`  | real | Trips dashboard after logging in as the seeded `demo@sydneytrips.dev` user, with the seeded "Group trip to Palm Beach" card visible. |
| `03-planning-canvas.svg`  | mockup | The full planning view — destination chip, weight sliders, participant + node markers on a basemap, the three-card Pareto carousel, the objective term breakdown, the lock button. |
| `04-driver-view.svg`      | mockup | The live driver view: route polyline through three pickups to Palm Beach, ETA HUD, ordered manifest, live SignalR events feed. |
| `05-cost-split.svg`       | mockup | The cost-split breakdown after a locked trip — fuel + tolls split by passenger-kilometres for every participant, with the inputs card on top and CSV/email export actions. |
| `06-whatif-diff.svg`      | mockup | The what-if modal: scenario inputs on the left (drop two passengers, re-weight optionally), diff on the right (objective drop, term-by-term change, removed and re-routed stops, solver stats), lock-the-candidate button. |

The two real PNGs are captured by `web/tests/screenshots.spec.ts` against the seeded demo trip. The SVG mockups stand in for views that depend on a frontend↔API contract that is currently drifting — see the top-level [`README.md`](../../README.md#status) for the known-issue note. The numbers and routes in the mockups come from real data emitted by the backend (you can verify them by hitting the API endpoints directly with the demo JWT).

## Regenerating the real screenshots

```bash
# 1. Bring up the stack
docker compose -f infra/docker-compose.yml up -d
dotnet ef database update --project src/Trips.Data --startup-project src/Trips.Api

# 2. Run the API in one terminal
dotnet run --project src/Trips.Api

# 3. Run the Next.js dev server in another terminal
cd web && npm run dev

# 4. Seed a deterministic demo trip
./tests/seed/seed-demo.sh                       # writes /tmp/seed-demo.json

# 5. Capture screenshots
cd web && npx playwright test tests/screenshots.spec.ts --headed
```

The spec reads `/tmp/seed-demo.json`, logs in, navigates to each page, and writes the PNGs back into this folder. It will currently capture six PNGs (`00-login`, `01-trips-dashboard`, and four "broken" ones that show the Next.js error overlay or empty-state cards for the trip-detail-dependent views). The four "broken" PNGs are deleted from this folder so the README references the SVG mockups instead.

## Editing the mockups

The SVGs are hand-written and lint-clean. Open them in any text editor or a vector tool. They use system fonts and Tailwind-ish neutral palette so they sit cohesively next to the real screenshots when rendered side-by-side in GitHub's markdown viewer.

## Mapbox token

If `NEXT_PUBLIC_MAPBOX_TOKEN` is unset in `web/.env.local`, the map components show a placeholder card rather than the basemap. The real screenshots (login + dashboard) don't depend on Mapbox; the planning + driver mockups have an inline stylised Sydney-ish coastline so they don't need a token either. To get full-fidelity captures of the planning canvas once the trip-detail wiring lands, set the token first:

```bash
echo "NEXT_PUBLIC_MAPBOX_TOKEN=pk.<your-public-mapbox-token>" >> web/.env.local
```

## Why these six

These are the screenshots referenced from the top-level README:

- **Login + dashboard** are the entry points and currently work end-to-end.
- **Planning + Pareto** is the interactive heart of the product. The mockup reflects the real Pareto solutions, weight sliders, and objective breakdown.
- **Driver view** is the moment-of-truth output — what someone actually uses on the road. The mockup shows the live SignalR events feed because that's the most non-obvious technical piece.
- **Cost split** is a small but distinctive feature that shows the depth of follow-through past "we solved the routing problem".
- **What-if diff** demonstrates the CP-SAT warm-start re-solve, which is the most technically interesting feature on the backend after the solver itself.
