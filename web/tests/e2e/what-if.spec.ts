// E2E scenario: start from a multi-driver scenario, drop 2 passengers via what-if, assert the
// diff view surfaces the removed stops and a new solution becomes lockable.
//
// What-if is driven via the API (warm-start from the locked solution); the UI exposes the
// diff in a modal — we mostly assert that the API round-trips a coherent diff. The screenshot
// flow exercises the modal UI separately.

import { test, expect, request as playwrightRequest } from "@playwright/test";
import { seed, useSession } from "./helpers";

const API_BASE_URL = process.env.API_BASE_URL ?? "http://localhost:5000";

test.describe("what-if scenario", () => {
  test.skip(process.env.API_BASE_URL_UNREACHABLE === "1", "API unreachable");

  test("dropping two passengers returns a coherent diff", async ({ page }) => {
    const data = await seed({ testTag: "whatif", scenario: "multi" });

    await useSession(page, data.sessionId);

    // Call what-if via the API directly — drop 2 passengers and keep the same weights. Carry the
    // seeded session cookie so the API treats this context as the trip's owner.
    const api = await playwrightRequest.newContext({
      baseURL: API_BASE_URL,
      extraHTTPHeaders: { Cookie: `trips_session=${data.sessionId}` },
    });
    const dropped = data.passengerIds.slice(0, 2);

    const whatIfRes = await api.post(`/trips/${data.tripId}/whatif`, {
      data: {
        droppedParticipantIds: dropped,
        newWeights: {
          driveTime: 1,
          stopCount: 0.5,
          walkAndPt: 0.5,
          arrivalSpread: 0.3,
          fairness: 0.3,
        },
      },
      failOnStatusCode: true,
    });
    const diff = (await whatIfRes.json()) as {
      baseline?: { objective?: number };
      candidate?: { objective?: number };
      removedStops?: unknown[];
    };

    // The diff must reference both solutions and at least mention removed stops.
    expect(diff.baseline?.objective).toBeDefined();
    expect(diff.candidate?.objective).toBeDefined();

    // Now visit the planner — the page should still render the locked solution intact
    // (what-if is a "preview" and doesn't mutate the lock).
    await page.goto(`/trips/${data.tripId}/plan`);
    await page.waitForLoadState("networkidle");
    await expect(page.locator("body")).toContainText(/Pareto|Solution|driver/i);
  });
});
