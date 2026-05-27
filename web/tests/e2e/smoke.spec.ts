import { test, expect } from "@playwright/test";

// UI-only smoke: boots the Next.js dev server (no real backend — the API base URL points
// nowhere usable in the test env) and checks the route plumbing. Auth was removed, so there's
// no login page and proxy.ts is a no-op: every route is reachable and a browser gets its
// anonymous `trips_session` cookie from the API on the first data call. We only assert that the
// routes resolve without bouncing to a (now non-existent) /login. Full end-to-end coverage with
// the real API lives in the seed-backed specs (single-driver / multi-driver / what-if).

test("root redirects to the trips list", async ({ page }) => {
  await page.goto("/");
  await expect(page).toHaveURL(/\/trips/);
});

test("trips list is reachable without signing in", async ({ page }) => {
  await page.goto("/trips");
  await expect(page).toHaveURL(/\/trips/);
  await expect(page).not.toHaveURL(/\/login/);
});

test("driver / passenger / cost routes resolve without a login redirect", async ({ page }) => {
  const tripId = "11111111-1111-1111-1111-111111111111";
  for (const sub of ["driver", "passenger", "cost"]) {
    await page.goto(`/trips/${tripId}/${sub}`);
    await expect(page).not.toHaveURL(/\/login/);
  }
});
