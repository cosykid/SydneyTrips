# Screenshots

Source images for the top-level `README.md` and the architecture notes. Regenerated via Playwright against a seeded demo trip.

## Files

| File | What it shows |
| ---- | ------------- |
| `01-planning-canvas.png` | The full planning view — destination chip, weight sliders, participant list, Pareto carousel, the Sydney basemap with driver/passenger markers. |
| `02-pareto-locked.png`   | After locking the balanced Pareto solution, with the chosen driver routes highlighted on the map. |
| `03-driver-view.png`     | The live driver view: route polyline, ordered manifest of pickups, current-leg ETA. |
| `04-cost-split.png`      | The cost-split breakdown card after a locked trip — fuel + tolls split by passenger-distance. |
| `05-whatif-diff.png`     | The what-if modal showing the diff between baseline and candidate solutions after dropping two passengers. |

## Regenerating

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

The Playwright spec at `web/tests/screenshots.spec.ts` reads `/tmp/seed-demo.json`, logs in, navigates to each page, and writes the PNGs back into this folder.

## Mapbox token

If `NEXT_PUBLIC_MAPBOX_TOKEN` is unset in `web/.env.local`, the map components show a placeholder card rather than the basemap. The screenshots still render coherently — they just lose the styled Sydney background. To get full-fidelity captures, set the token first:

```bash
echo "NEXT_PUBLIC_MAPBOX_TOKEN=pk.<your-public-mapbox-token>" >> web/.env.local
```

## Why these five

These are the screenshots referenced from the README:

- **Planning + Pareto** is the interactive heart of the product. Reviewers see the multi-objective trade-off curve and that the optimisation actually produced something visible on a real map.
- **Driver view** is the moment-of-truth output — what someone actually uses on the road.
- **Cost split** is a small but distinctive feature that shows the depth of follow-through past "we solved the routing problem".
- **What-if diff** demonstrates the warm-start re-solve, which is the most technically interesting feature on the backend after the solver itself.
