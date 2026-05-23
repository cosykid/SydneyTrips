// Categorical palette for driver routes (Okabe-Ito subset — colorblind-safe).
export const DRIVER_PALETTE = [
  "#0072B2", // blue
  "#D55E00", // vermillion
  "#009E73", // bluish-green
  "#CC79A7", // reddish-purple
  "#F0E442", // yellow
  "#56B4E9", // sky-blue
  "#E69F00", // orange
] as const;

export function driverColour(index: number): string {
  return DRIVER_PALETTE[index % DRIVER_PALETTE.length];
}
