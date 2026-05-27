import type { CandidateNode } from "@/lib/api/schema";

export type Modality = NonNullable<CandidateNode["modality"]>;

/** Visual identity for one Transport for NSW mode, mirroring the colours used on
 *  Sydney transport signage and inside Google Maps' transit overlay. The single-letter
 *  `mark` is rendered on a rounded square the way the official mode logos are (T / B / F / L). */
export interface TransitStyle {
  /** Mode colour — the badge background and the colour of the passenger's PT leg line. */
  color: string;
  /** Text colour for the badge, chosen for contrast against `color`. */
  textColor: string;
  /** Mode mark as shown on Sydney signage (T = train, B = bus, F = ferry, L = light rail). */
  mark: string;
  /** Human-readable mode name for tooltips / aria labels. */
  name: string;
}

// Transport for NSW mode colours (per the TfNSW brand palette).
const STYLES: Record<Modality, TransitStyle | null> = {
  train_station: { color: "#F6891F", textColor: "#FFFFFF", mark: "T", name: "Train" },
  bus_stop: { color: "#00B5EF", textColor: "#FFFFFF", mark: "B", name: "Bus" },
  ferry_wharf: { color: "#5AB031", textColor: "#FFFFFF", mark: "F", name: "Ferry" },
  light_rail: { color: "#EE343F", textColor: "#FFFFFF", mark: "L", name: "Light Rail" },
  // A "generic" hub carries no specific mode, so it gets no transit identity.
  generic: null,
};

/** Resolve the TfNSW style for a hub modality, or `null` when the hub has no transit mode
 *  (a plain pickup point, or a legacy node with no modality recorded). */
export function transitStyle(
  modality: CandidateNode["modality"] | undefined,
): TransitStyle | null {
  if (!modality) return null;
  return STYLES[modality] ?? null;
}

/** Colour for a passenger's public-transport leg when the hub's specific mode is unknown
 *  (legacy nodes with PT minutes but no modality). Distinct from every driver palette entry. */
export const PT_FALLBACK_COLOUR = "#7C3AED";

/** Slate used for the walking portion of a pickup leg, independent of mode. */
export const WALK_COLOUR = "#475569";

/** Human-readable name for a raw TfNSW leg mode string, for the itinerary's per-leg label. */
export function legModeName(mode: string): string {
  switch (mode.toLowerCase()) {
    case "walk":
      return "Walk";
    case "train":
    case "rail":
    case "heavy_rail":
      return "Train";
    case "metro":
      return "Metro";
    case "bus":
      return "Bus";
    case "ferry":
    case "wharf":
      return "Ferry";
    case "lightrail":
    case "light_rail":
    case "tram":
      return "Light Rail";
    default:
      return "Transit";
  }
}

/** Single-letter mark for a PT leg's mode chip (T / M / B / F / L), matching Sydney signage.
 *  Returns null for walk legs (which use a pedestrian glyph instead of a chip). */
export function legMark(mode: string): string | null {
  switch (mode.toLowerCase()) {
    case "walk":
      return null;
    case "metro":
      return "M";
    case "bus":
      return "B";
    case "ferry":
    case "wharf":
      return "F";
    case "lightrail":
    case "light_rail":
    case "tram":
      return "L";
    case "train":
    case "rail":
    case "heavy_rail":
      return "T";
    default:
      return "T";
  }
}

/** Style for one mode-tagged journey segment, keyed by the raw TfNSW mode string
 *  ("walk" | "train" | "metro" | "bus" | "ferry" | "lightrail" | …). Walk segments render as a
 *  dashed slate line; each PT mode gets its TfNSW colour. Unknown modes fall back to violet. */
export function legStyle(mode: string): { color: string; isWalk: boolean } {
  switch (mode.toLowerCase()) {
    case "walk":
      return { color: WALK_COLOUR, isWalk: true };
    case "train":
    case "rail":
    case "heavy_rail":
      return { color: "#F6891F", isWalk: false };
    case "metro":
      return { color: "#168388", isWalk: false };
    case "bus":
      return { color: "#00B5EF", isWalk: false };
    case "ferry":
    case "wharf":
      return { color: "#5AB031", isWalk: false };
    case "lightrail":
    case "light_rail":
    case "tram":
      return { color: "#EE343F", isWalk: false };
    default:
      return { color: PT_FALLBACK_COLOUR, isWalk: false };
  }
}
