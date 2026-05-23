// Pure helpers for building a WhatIfRequest payload + diffing the resulting
// solution against the original locked one. Lives in /lib so it can be unit
// tested in isolation from the dialog UI.

import type {
  ObjectiveWeights,
  Solution,
  WhatIfAddParticipant,
  WhatIfRequest,
} from "@/lib/api/schema";

export interface WhatIfDraft {
  dropParticipantIds: Set<string>;
  addParticipants: WhatIfAddParticipant[];
  newWeights?: ObjectiveWeights;
  repair: boolean;
}

export function emptyDraft(): WhatIfDraft {
  return {
    dropParticipantIds: new Set(),
    addParticipants: [],
    repair: true,
  };
}

export function isDraftEmpty(draft: WhatIfDraft): boolean {
  return (
    draft.dropParticipantIds.size === 0 &&
    draft.addParticipants.length === 0 &&
    draft.newWeights === undefined
  );
}

export function buildWhatIfRequest(draft: WhatIfDraft): WhatIfRequest {
  return {
    dropParticipantIds: Array.from(draft.dropParticipantIds),
    addParticipants: draft.addParticipants,
    newWeights: draft.newWeights,
    repair: draft.repair,
  };
}

export interface StopDiffEntry {
  /** Stable key — candidateNodeId when available, else lat/lng. */
  key: string;
  lat: number;
  lng: number;
  passengerIds: string[];
  state: "kept" | "added" | "removed";
}

/**
 * Diff two solutions' stops. Returns a flat list classified into kept/added/
 * removed. The map renders each set with a different colour.
 *
 * Stops are matched by `candidateNodeId` when set, falling back to a quantised
 * lat/lng key so we don't lose ground over small geometric noise.
 */
export function diffSolutionStops(original: Solution, next: Solution): StopDiffEntry[] {
  const keyOf = (s: { candidateNodeId?: string; location: { lat: number; lng: number } }): string =>
    s.candidateNodeId ?? `${s.location.lat.toFixed(4)},${s.location.lng.toFixed(4)}`;

  const originalStops = new Map<string, StopDiffEntry>();
  for (const r of original.routes) {
    for (const s of r.stops) {
      const key = keyOf(s);
      originalStops.set(key, {
        key,
        lat: s.location.lat,
        lng: s.location.lng,
        passengerIds: [...s.passengerIds],
        state: "removed", // assume removed; we'll flip to "kept" if matched
      });
    }
  }
  const out: StopDiffEntry[] = [];
  for (const r of next.routes) {
    for (const s of r.stops) {
      const key = keyOf(s);
      const existing = originalStops.get(key);
      if (existing) {
        existing.state = "kept";
        out.push({
          key,
          lat: s.location.lat,
          lng: s.location.lng,
          passengerIds: [...s.passengerIds],
          state: "kept",
        });
      } else {
        out.push({
          key,
          lat: s.location.lat,
          lng: s.location.lng,
          passengerIds: [...s.passengerIds],
          state: "added",
        });
      }
    }
  }
  // Surface remaining "removed" originals.
  for (const e of originalStops.values()) {
    if (e.state === "removed") out.push(e);
  }
  return out;
}

export interface MetricsDiff {
  drivingMinutesDelta: number;
  stopsDelta: number;
  totalWalkMetresDelta: number;
  fairnessDelta: number;
}

export function diffMetrics(original: Solution, next: Solution): MetricsDiff {
  return {
    drivingMinutesDelta: next.metrics.totalDrivingMinutes - original.metrics.totalDrivingMinutes,
    stopsDelta: next.metrics.totalStops - original.metrics.totalStops,
    totalWalkMetresDelta: next.metrics.totalWalkMetres - original.metrics.totalWalkMetres,
    fairnessDelta: next.metrics.fairnessIndex - original.metrics.fairnessIndex,
  };
}
