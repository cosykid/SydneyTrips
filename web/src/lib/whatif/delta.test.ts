import { describe, expect, it } from "vitest";
import {
  buildWhatIfRequest,
  diffMetrics,
  diffSolutionStops,
  emptyDraft,
  isDraftEmpty,
} from "./delta";
import type { Solution } from "@/lib/api/schema";

function solution(stops: Array<{ id?: string; lat: number; lng: number; pax: string[] }>): Solution {
  return {
    id: "sol-x",
    label: "fastest",
    metrics: {
      totalDrivingMinutes: 30,
      maxDrivingMinutes: 20,
      totalStops: stops.length,
      totalWalkMetres: 500,
      maxWalkMetres: 300,
      maxJourneyMinutes: 35,
      maxJourneyParticipantName: "Driver",
      fairnessIndex: 0.8,
    },
    routes: [
      {
        driverParticipantId: "d-1",
        driverDisplayName: "Driver",
        colour: "#000",
        polyline: stops.map((s) => ({ lat: s.lat, lng: s.lng })),
        stops: stops.map((s) => ({
          candidateNodeId: s.id,
          location: { lat: s.lat, lng: s.lng },
          arriveAt: new Date().toISOString(),
          pickupLegs: s.pax.map((pid) => ({ participantId: pid, walkMins: 1, ptMins: 0 })),
          passengerIds: s.pax,
          walkMetres: 100,
        })),
        drivingMinutes: 25,
        drivingDistanceKm: 18,
      },
    ],
  };
}

describe("whatif/delta", () => {
  it("emptyDraft is empty and round-trips through buildWhatIfRequest", () => {
    const draft = emptyDraft();
    expect(isDraftEmpty(draft)).toBe(true);
    const req = buildWhatIfRequest(draft);
    expect(req.dropParticipantIds).toEqual([]);
    expect(req.addParticipants).toEqual([]);
    expect(req.newWeights).toBeUndefined();
    expect(req.repair).toBe(true);
  });

  it("isDraftEmpty flips false once a drop is added", () => {
    const draft = emptyDraft();
    draft.dropParticipantIds.add("p-1");
    expect(isDraftEmpty(draft)).toBe(false);
  });

  it("buildWhatIfRequest serialises drop sets to arrays", () => {
    const draft = emptyDraft();
    draft.dropParticipantIds.add("p-1");
    draft.dropParticipantIds.add("p-2");
    const req = buildWhatIfRequest(draft);
    expect(req.dropParticipantIds.sort()).toEqual(["p-1", "p-2"]);
  });

  it("classifies stops as kept/added/removed", () => {
    const before = solution([
      { id: "n-1", lat: -33.86, lng: 151.2, pax: ["p-1"] },
      { id: "n-2", lat: -33.88, lng: 151.21, pax: ["p-2"] },
    ]);
    const after = solution([
      { id: "n-1", lat: -33.86, lng: 151.2, pax: ["p-1"] }, // kept
      { id: "n-3", lat: -33.9, lng: 151.22, pax: ["p-3"] }, // added
      // n-2 removed
    ]);
    const diff = diffSolutionStops(before, after);
    expect(diff.find((d) => d.key === "n-1")?.state).toBe("kept");
    expect(diff.find((d) => d.key === "n-2")?.state).toBe("removed");
    expect(diff.find((d) => d.key === "n-3")?.state).toBe("added");
  });

  it("matches by lat/lng key when candidateNodeId is missing", () => {
    const before = solution([{ lat: -33.86, lng: 151.2, pax: ["p-1"] }]);
    const after = solution([{ lat: -33.86, lng: 151.2, pax: ["p-1"] }]);
    const diff = diffSolutionStops(before, after);
    expect(diff).toHaveLength(1);
    expect(diff[0].state).toBe("kept");
  });

  it("diffMetrics computes per-axis deltas", () => {
    const before = solution([{ lat: 0, lng: 0, pax: [] }]);
    const after: Solution = {
      ...before,
      metrics: {
        ...before.metrics,
        totalDrivingMinutes: 40,
        totalStops: 3,
        totalWalkMetres: 300,
        fairnessIndex: 0.9,
      },
    };
    const d = diffMetrics(before, after);
    expect(d.drivingMinutesDelta).toBe(10);
    expect(d.stopsDelta).toBe(2);
    expect(d.totalWalkMetresDelta).toBe(-200);
    expect(d.fairnessDelta).toBeCloseTo(0.1);
  });
});
