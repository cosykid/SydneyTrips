// Captures the five hero screenshots embedded in the top-level README.
//
// Workflow:
//   1. ./tests/seed/seed-demo.sh                      (creates a deterministic demo trip)
//   2. cd web && npm run dev                          (or your usual local Next.js server)
//   3. cd web && npx playwright test tests/screenshots.spec.ts --headed
//
// Outputs land in docs/screenshots/*.png — paths are relative to the repo root.
//
// The Mapbox token is read from NEXT_PUBLIC_MAPBOX_TOKEN. If unset, the map components fall
// back to a placeholder card explaining the missing token — the screenshots still render
// cleanly enough for the README; we just lose the basemap.

import { test, expect } from "@playwright/test";
import { promises as fs } from "node:fs";
import path from "node:path";
import { loginViaUi } from "./e2e/helpers";

const SEED_PATH = process.env.DEMO_SEED_PATH ?? "/tmp/seed-demo.json";
const SHOTS_DIR = path.resolve(__dirname, "..", "..", "docs", "screenshots");

interface SeedFile {
  email: string;
  password: string;
  tripId: string;
  runId: string;
  lockedSolutionId: string;
  driverIds: string[];
  passengerIds: string[];
}

test.use({ viewport: { width: 1440, height: 900 } });

test.beforeAll(async () => {
  await fs.mkdir(SHOTS_DIR, { recursive: true });
});

test.describe.serial("hero screenshots", () => {
  let seed: SeedFile;

  test.beforeAll(async () => {
    try {
      const raw = await fs.readFile(SEED_PATH, "utf8");
      seed = JSON.parse(raw) as SeedFile;
    } catch (err) {
      throw new Error(
        `seed file not found at ${SEED_PATH}. Run tests/seed/seed-demo.sh first. (${String(err)})`,
      );
    }
  });

  test("00 — login page (unauthenticated)", async ({ page }) => {
    await page.goto("/login");
    await page.waitForLoadState("networkidle");
    await expect(page.getByRole("heading", { name: "Sign in" })).toBeVisible();
    await page.screenshot({
      path: path.join(SHOTS_DIR, "00-login.png"),
      fullPage: true,
    });
  });

  test("01 — trips dashboard", async ({ page }) => {
    await loginViaUi(page, seed.email, seed.password);
    await page.goto("/trips");
    await page.waitForLoadState("networkidle");
    await page.waitForTimeout(1_500);
    await page.screenshot({
      path: path.join(SHOTS_DIR, "01-trips-dashboard.png"),
      fullPage: true,
    });
  });

  test("02 — trip overview", async ({ page }) => {
    await loginViaUi(page, seed.email, seed.password);
    await page.goto(`/trips/${seed.tripId}`);
    await page.waitForLoadState("networkidle");
    await page.waitForTimeout(1_500);
    await page.screenshot({
      path: path.join(SHOTS_DIR, "02-trip-overview.png"),
      fullPage: true,
    });
  });

  test("03 — planning canvas", async ({ page }) => {
    await loginViaUi(page, seed.email, seed.password);
    await page.goto(`/trips/${seed.tripId}/plan`);
    await page.waitForLoadState("networkidle");
    // Give Mapbox a beat to settle markers + camera; harmless if the placeholder card is up.
    await page.waitForTimeout(2_500);
    await page.screenshot({
      path: path.join(SHOTS_DIR, "03-planning-canvas.png"),
      fullPage: true,
    });
  });

  test("04 — driver view", async ({ page }) => {
    await loginViaUi(page, seed.email, seed.password);
    const driverId = seed.driverIds[0];
    await page.goto(`/trips/${seed.tripId}/driver?as=${driverId}`);
    await page.waitForLoadState("networkidle");
    await page.waitForTimeout(2_500);
    await page.screenshot({
      path: path.join(SHOTS_DIR, "04-driver-view.png"),
      fullPage: true,
    });
  });

  test("05 — cost split", async ({ page }) => {
    await loginViaUi(page, seed.email, seed.password);
    await page.goto(`/trips/${seed.tripId}/cost`);
    await page.waitForLoadState("networkidle");
    await page.waitForTimeout(1_500);
    await page.screenshot({
      path: path.join(SHOTS_DIR, "05-cost-split.png"),
      fullPage: true,
    });
  });

  test("06 — what-if dialog", async ({ page }) => {
    // Walks the locked-solution path: open planner → re-run optimisation →
    // wait for the Pareto carousel → open the what-if modal on the locked
    // candidate. The seeded trip has paretoIndex 0 locked already, and the
    // store's activeRunId is per-session — so we trigger a fresh run here
    // to populate the carousel.
    await loginViaUi(page, seed.email, seed.password);
    await page.goto(`/trips/${seed.tripId}/plan`);
    await page.waitForLoadState("networkidle");
    await page.getByRole("button", { name: /^Plan trip$/i }).click();
    // Wait for the carousel to populate. The Pareto query polls a few times
    // before the runner reaches `completed`, so this can take ~5–10s.
    await page.getByTestId("pareto-carousel").waitFor({ timeout: 30_000 });
    // The "Try changes" button only renders on the locked tile. The seed locked
    // paretoIndex 0, so click the first tile's action.
    const whatIfButton = page.getByRole("button", { name: /Try changes/i }).first();
    await whatIfButton.click();
    await page.waitForTimeout(1_500);
    await page.screenshot({
      path: path.join(SHOTS_DIR, "06-whatif-diff.png"),
      fullPage: true,
    });
  });
});
