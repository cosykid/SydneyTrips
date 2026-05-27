// Regression test for the WS8 trip-detail eager-loading fix.
//
// Before WS8: GET /trips/{id} returned a flat trip; the frontend's
// PlanCanvas / TripOverview / DriverView / CostBreakdown components silently
// rendered an empty participants list because `participants[]` wasn't on the
// wire. The README screenshots were SVG mockups.
//
// After WS8: GET /trips/{id} returns participants[] with each participant's
// candidateNodes[] eager-loaded, and the FE hooks adapt the API DTO into
// the UI's nested-LatLng shape. This test asserts that participants seeded
// via the API show up on the trip-detail page AND the planner page.
//
// Skipped automatically unless the .NET API is reachable on $API_BASE_URL.

import { test, expect } from "@playwright/test";
import { seed, loginViaUi } from "./helpers";

test.describe("trip-detail eager-loading", () => {
  test.skip(process.env.API_BASE_URL_UNREACHABLE === "1", "API unreachable");

  test("planner renders participants from real trip data", async ({ page }) => {
    const data = await seed({ testTag: "participants", scenario: "single" });

    await loginViaUi(page, data.email, data.password);

    // Trip overview should list all the seeded participants (1 driver + 3 passengers).
    await page.goto(`/trips/${data.tripId}`);
    await page.waitForLoadState("networkidle");

    // The PARTICIPANTS dt/dd cell on the overview card.
    const participantBadge = page.getByText(/^\s*4\s*$/).first();
    await expect(participantBadge).toBeVisible({ timeout: 10_000 });

    // The participant list card shows each name from helpers.ts.
    await expect(page.getByText("Alex (Bondi)")).toBeVisible();
    await expect(page.getByText("Eli (Coogee)")).toBeVisible();
    await expect(page.getByText("Fiona (Manly)")).toBeVisible();
    await expect(page.getByText("Gus (Mosman)")).toBeVisible();

    // Planner page header reports the participant + candidate-node counts.
    await page.goto(`/trips/${data.tripId}/plan`);
    await page.waitForLoadState("networkidle");
    // The header reads "N participants · M candidate nodes" — N must be 4 for
    // the single scenario. If the API drift returns to flat trips, this fails
    // because the participants array is empty and the header reads "0 participants".
    await expect(
      page.getByText(/4 participants .* candidate nodes/i),
    ).toBeVisible({ timeout: 15_000 });

    // The map fallback canvas (used when no Google Maps key is configured) renders
    // a labelled origin for every participant. We assert the driver label is
    // visible — if participants[] were empty, it wouldn't be.
    const fallback = page.getByTestId("map-fallback");
    if (await fallback.isVisible()) {
      await expect(fallback).toContainText("Alex (Bondi)");
    }
  });
});
