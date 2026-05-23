import { test, expect } from "@playwright/test";

// Smoke test: boots the Next.js dev server and verifies the unauth flow.
// We do *not* hit a real backend — the API base URL points nowhere usable in
// the test env, and the /trips/* routes are gated by proxy.ts on the
// session cookie. Full end-to-end coverage with the real API is a TODO once
// WS4 lands and infra/docker-compose is wired up.

test("login page renders and trips redirects when unauthenticated", async ({ page }) => {
  await page.goto("/login");
  await expect(page.getByRole("heading", { name: "Sign in" })).toBeVisible();
  await expect(page.getByRole("button", { name: "Sign in" })).toBeVisible();

  // Navigating to /trips without a session cookie should bounce back to /login.
  await page.goto("/trips");
  await expect(page).toHaveURL(/\/login/);
});
