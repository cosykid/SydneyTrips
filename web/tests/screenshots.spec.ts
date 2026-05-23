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

  test("01 — planning canvas", async ({ page }) => {
    await loginViaUi(page, seed.email, seed.password);
    await page.goto(`/trips/${seed.tripId}/plan`);
    await page.waitForLoadState("networkidle");
    // Give Mapbox a beat to settle markers + camera; harmless if the placeholder card is up.
    await page.waitForTimeout(2_500);
    await page.screenshot({
      path: path.join(SHOTS_DIR, "01-planning-canvas.png"),
      fullPage: true,
    });
  });

  test("02 — pareto locked", async ({ page }) => {
    await loginViaUi(page, seed.email, seed.password);
    await page.goto(`/trips/${seed.tripId}/plan?paretoIndex=0`);
    await page.waitForLoadState("networkidle");
    await page.waitForTimeout(2_500);
    await page.screenshot({
      path: path.join(SHOTS_DIR, "02-pareto-locked.png"),
      fullPage: true,
    });
  });

  test("03 — driver view", async ({ page }) => {
    await loginViaUi(page, seed.email, seed.password);
    const driverId = seed.driverIds[0];
    await page.goto(`/trips/${seed.tripId}/driver?as=${driverId}`);
    await page.waitForLoadState("networkidle");
    await page.waitForTimeout(2_500);
    await page.screenshot({
      path: path.join(SHOTS_DIR, "03-driver-view.png"),
      fullPage: true,
    });
  });

  test("04 — cost split", async ({ page }) => {
    await loginViaUi(page, seed.email, seed.password);
    await page.goto(`/trips/${seed.tripId}/cost`);
    await page.waitForLoadState("networkidle");
    await page.waitForTimeout(1_500);
    await expect(page.getByRole("heading", { name: /cost split/i })).toBeVisible();
    await page.screenshot({
      path: path.join(SHOTS_DIR, "04-cost-split.png"),
      fullPage: true,
    });
  });

  test("05 — what-if diff", async ({ page }) => {
    await loginViaUi(page, seed.email, seed.password);
    await page.goto(`/trips/${seed.tripId}/plan?whatif=open&drop=${seed.passengerIds[0]}`);
    await page.waitForLoadState("networkidle");
    await page.waitForTimeout(2_500);
    // The what-if modal is opened via the planner UI; the deep-link above is a hint to any
    // future router-aware open state. If the modal isn't visible from the URL alone, fall
    // back to clicking the "what-if" button if one is present.
    const trigger = page.getByRole("button", { name: /what.?if/i }).first();
    if (await trigger.isVisible().catch(() => false)) {
      await trigger.click();
      await page.waitForTimeout(1_500);
    }
    await page.screenshot({
      path: path.join(SHOTS_DIR, "05-whatif-diff.png"),
      fullPage: true,
    });
  });
});
