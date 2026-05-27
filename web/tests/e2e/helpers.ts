// Shared helpers for the WS8 e2e suite. The Playwright runs assume:
//   - infra/docker-compose.yml is up (Postgres + Redis healthy)
//   - dotnet ef database update has been applied
//   - Trips.Api is reachable at API_BASE_URL (default http://localhost:5000)
//   - Next.js dev server is reachable at the Playwright baseURL (http://localhost:3000)
//
// The Playwright webServer config in playwright.config.ts boots Next.js. The .NET API is
// expected to be running already (it's slow to cold-start and the seed script needs to
// hit it before tests run anyway). The CI workflow brings up docker compose + the API as
// a separate job step.

import { request as playwrightRequest, type APIRequestContext, type Page } from "@playwright/test";

const API_BASE_URL = process.env.API_BASE_URL ?? "http://localhost:5000";

export interface SeedResult {
  baseUrl: string;
  /** The anonymous-session GUID (value of the `trips_session` cookie) that owns the seeded trip.
   *  Inject it into a browser context with {@link useSession} so the UI sees the same trip. */
  sessionId: string;
  tripId: string;
  runId: string;
  lockedSolutionId: string;
  driverIds: string[];
  passengerIds: string[];
}

interface SeedOptions {
  // Unique-per-test suffix appended to the demo email so concurrent specs don't collide.
  testTag: string;
  scenario: "single" | "multi";
}

// Sydney sample coords (lng, lat) — same set as tests/seed/seed-demo.sh.
const DRIVERS = [
  { name: "Alex (Bondi)", lng: 151.2767, lat: -33.8915, seats: 4 },
  { name: "Bianca (Chatswood)", lng: 151.1833, lat: -33.7969, seats: 4 },
  { name: "Cameron (Newtown)", lng: 151.1798, lat: -33.8975, seats: 3 },
  { name: "Dani (Hurstville)", lng: 151.1027, lat: -33.9663, seats: 4 },
];
const PASSENGERS = [
  { name: "Eli (Coogee)", lng: 151.2576, lat: -33.9211 },
  { name: "Fiona (Manly)", lng: 151.2868, lat: -33.7969 },
  { name: "Gus (Mosman)", lng: 151.2378, lat: -33.8281 },
  { name: "Hana (Surry Hills)", lng: 151.2125, lat: -33.8841 },
  { name: "Ivy (Glebe)", lng: 151.1856, lat: -33.8786 },
  { name: "Jaz (Marrickville)", lng: 151.1551, lat: -33.9099 },
  { name: "Kai (Rockdale)", lng: 151.1390, lat: -33.9522 },
  { name: "Leo (Strathfield)", lng: 151.0833, lat: -33.8736 },
  { name: "Mia (Parramatta)", lng: 151.0034, lat: -33.8150 },
  { name: "Nate (Burwood)", lng: 151.1037, lat: -33.8780 },
  { name: "Oli (Ashfield)", lng: 151.1244, lat: -33.8881 },
  { name: "Priya (Auburn)", lng: 151.0322, lat: -33.8492 },
];

export async function seed(options: SeedOptions): Promise<SeedResult> {
  // Auth is gone — the API stamps a `trips_session` cookie on the first request and this
  // request context's cookie jar reuses it on every subsequent call, so the whole seed runs
  // as one anonymous "browser". Prime the cookie up front, then read it back for the UI.
  const api = await playwrightRequest.newContext({ baseURL: API_BASE_URL });
  await api.get("/healthz");

  const departAt = new Date(Date.now() + 24 * 60 * 60 * 1000);
  const tripRes = await api.post("/trips", {
    data: {
      name: `E2E ${options.testTag} ${Date.now()}`,
      destinationName: "Palm Beach NSW",
      destinationLongitude: 151.3247,
      destinationLatitude: -33.5984,
      departAt: departAt.toISOString(),
      arrivalWindowEarliest: new Date(departAt.getTime() + 45 * 60_000).toISOString(),
      arrivalWindowLatest: new Date(departAt.getTime() + 75 * 60_000).toISOString(),
    },
    failOnStatusCode: true,
  });
  const trip = (await tripRes.json()) as { id: string };

  const nDrivers = options.scenario === "single" ? 1 : 3;
  const nPassengers = options.scenario === "single" ? 3 : 8;

  const driverIds: string[] = [];
  for (let i = 0; i < nDrivers; i++) {
    const d = DRIVERS[i];
    const r = await api.post(`/trips/${trip.id}/participants`, {
      data: {
        displayName: d.name,
        homeLongitude: d.lng,
        homeLatitude: d.lat,
        hasCar: true,
        seats: d.seats,
      },
      failOnStatusCode: true,
    });
    driverIds.push(((await r.json()) as { id: string }).id);
  }

  const passengerIds: string[] = [];
  for (let i = 0; i < nPassengers; i++) {
    const p = PASSENGERS[i];
    const r = await api.post(`/trips/${trip.id}/participants`, {
      data: {
        displayName: p.name,
        homeLongitude: p.lng,
        homeLatitude: p.lat,
        hasCar: false,
        seats: 0,
      },
      failOnStatusCode: true,
    });
    passengerIds.push(((await r.json()) as { id: string }).id);
  }

  const optRes = await api.post(`/trips/${trip.id}/optimise`, {
    data: {
      weights: { driveTime: 1, stopCount: 0.5, walkAndPt: 0.5, arrivalSpread: 0.3, fairness: 0.3 },
      solver: 1, // Heuristic — faster than OR-Tools and avoids the Phase B runner FK quirk.
    },
    failOnStatusCode: true,
  });
  const run = (await optRes.json()) as { runId: string };

  await waitForRun(api, trip.id, run.runId);

  const lockRes = await api.post(`/trips/${trip.id}/lock-solution`, {
    data: { runId: run.runId, paretoIndex: 0 },
    failOnStatusCode: true,
  });
  const lock = (await lockRes.json()) as { lockedSolutionId: string };

  return {
    baseUrl: API_BASE_URL,
    sessionId: await readSessionId(api),
    tripId: trip.id,
    runId: run.runId,
    lockedSolutionId: lock.lockedSolutionId,
    driverIds,
    passengerIds,
  };
}

/** Pull the `trips_session` cookie value out of the request context's jar. */
async function readSessionId(api: APIRequestContext): Promise<string> {
  const state = await api.storageState();
  const cookie = state.cookies.find((c) => c.name === "trips_session");
  if (!cookie) throw new Error("API never set a trips_session cookie");
  return cookie.value;
}

async function waitForRun(api: APIRequestContext, tripId: string, runId: string): Promise<void> {
  for (let i = 0; i < 120; i++) {
    const res = await api.get(`/trips/${tripId}/runs/${runId}`);
    if (res.ok()) {
      const body = (await res.json()) as { run: { status: string | number } };
      const status = String(body.run.status);
      if (status === "2" || status === "Completed") return;
      if (status === "3" || status === "Failed") throw new Error(`run ${runId} failed`);
    }
    await new Promise((r) => setTimeout(r, 500));
  }
  throw new Error(`run ${runId} did not finish within 60s`);
}

/** Make a browser context act as the anonymous session that owns the seeded trip, by injecting
 *  the same `trips_session` cookie. Call before navigating — there's no login page to drive. */
export async function useSession(page: Page, sessionId: string): Promise<void> {
  await page.context().addCookies([
    { name: "trips_session", value: sessionId, domain: "localhost", path: "/" },
  ]);
}
