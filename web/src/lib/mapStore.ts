import { create } from "zustand";
import type { LatLng } from "./api/schema";

/**
 * Drives the persistent Sydney map that sits *behind* every non-map page
 * (trips list, trip detail, create, cost). Those pages render as floating
 * overlays — the map is always visible — and publish what they're "about"
 * here so the backdrop can drop pins and recentre. Pages call `setFocus` on
 * mount and `clear` on unmount.
 *
 * The planner / driver / passenger routes own a richer map of their own, so
 * the backdrop hides itself there (see `MapBackdrop`) and ignores this store.
 */
export type MapPinKind = "destination" | "trip" | "home" | "driver";

export interface MapPin {
  id: string;
  position: LatLng;
  label?: string;
  kind: MapPinKind;
}

export interface MapFocus {
  pins: MapPin[];
  /** When set, the backdrop pans/zooms here. Omit to leave the camera alone. */
  center?: LatLng;
  zoom?: number;
}

interface MapBackdropState {
  focus: MapFocus;
  setFocus: (focus: MapFocus) => void;
  clear: () => void;
}

const EMPTY: MapFocus = { pins: [] };

export const useMapBackdrop = create<MapBackdropState>((set) => ({
  focus: EMPTY,
  setFocus: (focus) => set({ focus }),
  clear: () => set({ focus: EMPTY }),
}));
