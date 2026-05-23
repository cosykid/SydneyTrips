import { test, expect } from "@playwright/test";

// Smoke test: boots the Next.js dev server and verifies the unauth flow plus
// the route plumbing for the live driver/passenger views and the cost split
// page. We do *not* hit a real backend — the API base URL points nowhere
// usable in the test env, and the /trips/* routes are gated by proxy.ts on
// the session cookie. Full end-to-end coverage with the real API is a TODO
// once WS4/WS5 land and infra/docker-compose is wired up.

test("login page renders and trips redirects when unauthenticated", async ({ page }) => {
  await page.goto("/login");
  await expect(page.getByRole("heading", { name: "Sign in" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Sign in" })).toBeVisible();

  // Navigating to /trips without a session cookie should bounce back to /login.
  await page.goto("/trips");
  await expect(page).toHaveURL(/\/login/);
});

test("driver view redirects to login when unauthenticated", async ({ page }) => {
  await page.goto("/trips/11111111-1111-1111-1111-111111111111/driver");
  await expect(page).toHaveURL(/\/login/);
});

test("passenger view redirects to login when unauthenticated", async ({ page }) => {
  await page.goto("/trips/11111111-1111-1111-1111-111111111111/passenger");
  await expect(page).toHaveURL(/\/login/);
});

test("cost split view redirects to login when unauthenticated", async ({ page }) => {
  await page.goto("/trips/11111111-1111-1111-1111-111111111111/cost");
  await expect(page).toHaveURL(/\/login/);
});

test("realtime token endpoint requires session", async ({ request }) => {
  const res = await request.get("/api/realtime/token");
  // No session cookie sent → 401 from the route handler.
  expect(res.status()).toBe(401);
});
