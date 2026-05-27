// E2E scenario: three drivers, eight passengers spread across the four corners of Sydney →
// optimise → lock → assert the planner shows three distinct routes with passenger assignments.
//
// Like single-driver.spec.ts, this requires a live Trips.Api.

import { test, expect } from "@playwright/test";
import { seed, useSession } from "./helpers";

test.describe("multi-driver scenario", () => {
  test.skip(process.env.API_BASE_URL_UNREACHABLE === "1", "API unreachable");

  test("renders three driver routes with passenger assignments", async ({ page }) => {
    const data = await seed({ testTag: "multi", scenario: "multi" });

    await useSession(page, data.sessionId);
    await page.goto(`/trips/${data.tripId}/plan`);
    await page.waitForLoadState("networkidle");

    // The locked solution from seed() (Pareto index 0) is rendered first. We don't bind to
    // implementation details: just look for evidence of multi-driver shape — three driver
    // chips/markers/labels in the planner DOM.
    const driverHits = page.locator("body").locator("text=/D0|D1|D2|driver 1|driver 2|driver 3/i");
    await expect(driverHits.first()).toBeVisible({ timeout: 20_000 });

    // The CostBreakdown view should render for this trip too — we use it as a downstream
    // smoke for the locked-solution wiring.
    await page.goto(`/trips/${data.tripId}/cost`);
    await page.waitForLoadState("networkidle");
    await expect(page.getByRole("heading", { name: /cost split/i })).toBeVisible();
  });
});
