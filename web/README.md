# SydneyTrips web (Next.js)

Phase B scaffold of the SydneyTrips frontend: auth shell + trips CRUD +
map-based planning canvas. Live-tracking views (driver/passenger) ship in
Phase C.

## Stack

- Next.js 16 (App Router, Turbopack, React 19)
- TypeScript strict mode
- Tailwind v4 + shadcn/ui (neutral base, CSS variables)
- TanStack Query for server cache, Zustand for UI state
- Mapbox GL JS via `react-map-gl`
- Zod + react-hook-form for form validation
- Vitest + Testing Library + Playwright

## Getting started

```bash
cp .env.example .env.local
# fill NEXT_PUBLIC_MAPBOX_TOKEN, AUTH_SECRET, API_BASE_URL
npm install
npm run dev
```

The dev server listens on `http://localhost:3000`.

## Environment

| Var                       | Required | Notes                                                                 |
| ------------------------- | -------- | --------------------------------------------------------------------- |
| `NEXT_PUBLIC_API_BASE_URL`| yes      | Public URL of the Trips API (used by client-side preview helpers).    |
| `API_BASE_URL`            | no       | Server-side override for the proxy. Defaults to `NEXT_PUBLIC_API_BASE_URL`. |
| `NEXT_PUBLIC_MAPBOX_TOKEN`| yes      | Public Mapbox token. The map degrades to a notice when missing.       |
| `AUTH_SECRET`             | yes      | 32+ char random string used to sign session cookies (`openssl rand -base64 32`). |
| `SESSION_COOKIE_NAME`     | no       | Defaults to `trips_session`.                                          |

## Scripts

```bash
npm run dev          # Next.js dev server (Turbopack)
npm run build        # Production build
npm run lint         # ESLint (zero errors required)
npm run typecheck    # tsc --noEmit
npm run format       # Prettier
npm run test         # Vitest (component tests)
npm run test:e2e     # Playwright smoke
npm run gen:api      # Regenerate src/lib/api/types.ts from ../src/Trips.Api/openapi.json
```

`gen:api` is a graceful no-op when the OpenAPI spec hasn't shipped yet.

## API integration

The browser never sees the API JWT. Requests flow through the Next.js
route-handler proxy at `/api/proxy/[...path]/route.ts`, which reads the
httpOnly session cookie, unseals it, and forwards the bearer token to the
upstream Trips API.

- `src/lib/api/schema.ts` — hand-written types matching the WS4 endpoint list.
- `src/lib/api/client.ts` — fetch wrapper with `ApiError`, 401 redirect, JSON typing.
- `src/lib/api/hooks.ts` — TanStack Query hooks.

Replace `schema.ts` with the codegen output from `npm run gen:api` once
`src/Trips.Api/openapi.json` is committed by WS4.

## Map layers

`src/components/plan/PlanMap.tsx` renders five distinct GeoJSON sources:

| Source            | Layer type | Style                                                        |
| ----------------- | ---------- | ------------------------------------------------------------ |
| `candidate-nodes` | circle     | small grey dot, light stroke — all candidate PT pickup nodes.|
| `driver-routes`   | line       | per-driver categorical colour (Okabe-Ito palette).           |
| `chosen-nodes`    | circle     | green circle with dark green stroke — picked pickups.        |
| `origins`         | circle     | red dot, larger radius for drivers.                          |
| `destination`     | symbol     | star glyph, yellow halo.                                     |

The map degrades to an inline notice when `NEXT_PUBLIC_MAPBOX_TOKEN` is unset.

## Pages

- `/login`, `/register` — auth UI hitting our route handlers, which call
  `/auth/login` and `/auth/register` and seal the JWT into a session cookie.
- `/trips` — list user trips.
- `/trips/new` — create-trip form with geocode preview.
- `/trips/[id]` — overview, participant CRUD.
- `/trips/[id]/plan` — full-screen map + right-hand panel: weight sliders,
  optimise button, Pareto carousel.
- `/trips/[id]/cost` — placeholder cost-split breakdown.

`proxy.ts` (Next 16's renamed `middleware.ts`) optimistically redirects
unauthenticated requests on `/trips/*` to `/login` based on cookie presence;
actual token validation happens server-side inside the proxy handler.
