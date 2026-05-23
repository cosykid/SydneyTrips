"use client";

import { create } from "zustand";
import { DEFAULT_WEIGHTS, type ObjectiveWeights } from "./api/schema";

export const SYDNEY_CBD = { latitude: -33.8688, longitude: 151.2093, zoom: 11 };

export interface MapViewState {
  latitude: number;
  longitude: number;
  zoom: number;
}

export type PlanTab = "fastest" | "fewest_stops" | "least_walking";

export interface PlanState {
  weights: ObjectiveWeights;
  setWeight: (key: keyof ObjectiveWeights, value: number) => void;
  resetWeights: () => void;

  activeRunId: string | undefined;
  setActiveRunId: (id: string | undefined) => void;

  selectedSolutionId: string | undefined;
  selectSolution: (id: string | undefined) => void;

  selectedTab: PlanTab;
  setSelectedTab: (tab: PlanTab) => void;

  viewState: MapViewState;
  setViewState: (next: MapViewState) => void;
  resetView: () => void;
}

export const usePlanStore = create<PlanState>((set) => ({
  weights: { ...DEFAULT_WEIGHTS },
  setWeight: (key, value) =>
    set((state) => ({ weights: { ...state.weights, [key]: value } })),
  resetWeights: () => set({ weights: { ...DEFAULT_WEIGHTS } }),

  activeRunId: undefined,
  setActiveRunId: (id) => set({ activeRunId: id }),

  selectedSolutionId: undefined,
  selectSolution: (id) => set({ selectedSolutionId: id }),

  selectedTab: "fastest",
  setSelectedTab: (tab) => set({ selectedTab: tab }),

  viewState: { ...SYDNEY_CBD },
  setViewState: (next) => set({ viewState: next }),
  resetView: () => set({ viewState: { ...SYDNEY_CBD } }),
}));
