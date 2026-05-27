"use client";

import { create } from "zustand";
import { DEFAULT_WEIGHTS, type ObjectiveWeights } from "./api/schema";

export const SYDNEY_CBD = { latitude: -33.8688, longitude: 151.2093, zoom: 11 };

export interface MapViewState {
  latitude: number;
  longitude: number;
  zoom: number;
}

export type PlanTab = "fastest" | "fewest_stops" | "least_transit";

interface TripPlanSlice {
  runId?: string;
  solutionId?: string;
}

export interface PlanState {
  weights: ObjectiveWeights;
  setWeight: (key: keyof ObjectiveWeights, value: number) => void;
  resetWeights: () => void;

  // Per-trip plan state. Keyed by tripId so navigating between trips does not
  // carry a stale runId into the new trip's PlanCanvas (which would otherwise
  // poll /trips/<new>/runs/<old> in a tight 404 loop).
  byTrip: Record<string, TripPlanSlice>;
  getActiveRunId: (tripId: string) => string | undefined;
  setActiveRunId: (tripId: string, id: string | undefined) => void;
  getSelectedSolutionId: (tripId: string) => string | undefined;
  selectSolution: (tripId: string, id: string | undefined) => void;

  selectedTab: PlanTab;
  setSelectedTab: (tab: PlanTab) => void;

  viewState: MapViewState;
  setViewState: (next: MapViewState) => void;
  resetView: () => void;
}

export const usePlanStore = create<PlanState>((set, get) => ({
  weights: { ...DEFAULT_WEIGHTS },
  setWeight: (key, value) =>
    set((state) => ({ weights: { ...state.weights, [key]: value } })),
  resetWeights: () => set({ weights: { ...DEFAULT_WEIGHTS } }),

  byTrip: {},
  getActiveRunId: (tripId) => get().byTrip[tripId]?.runId,
  setActiveRunId: (tripId, id) =>
    set((state) => ({
      byTrip: {
        ...state.byTrip,
        [tripId]: { ...state.byTrip[tripId], runId: id },
      },
    })),
  getSelectedSolutionId: (tripId) => get().byTrip[tripId]?.solutionId,
  selectSolution: (tripId, id) =>
    set((state) => ({
      byTrip: {
        ...state.byTrip,
        [tripId]: { ...state.byTrip[tripId], solutionId: id },
      },
    })),

  selectedTab: "fastest",
  setSelectedTab: (tab) => set({ selectedTab: tab }),

  viewState: { ...SYDNEY_CBD },
  setViewState: (next) => set({ viewState: next }),
  resetView: () => set({ viewState: { ...SYDNEY_CBD } }),
}));
