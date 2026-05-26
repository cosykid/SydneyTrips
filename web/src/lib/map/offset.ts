import type { LatLng } from "@/lib/api/schema";

const EARTH_RADIUS_M = 6378137;
// Metres per pixel at zoom 0 on the equator (Web Mercator).
const METRES_PER_PIXEL_Z0 = 156543.03392;

/** Ground resolution (metres per screen pixel) at a given zoom and latitude. Lets a
 *  metre-based offset stay roughly constant in *pixels* across zoom levels. */
export function metresPerPixel(zoom: number, lat: number): number {
  return (METRES_PER_PIXEL_Z0 * Math.cos((lat * Math.PI) / 180)) / 2 ** zoom;
}

/**
 * Shift a path sideways by `metres`, perpendicular to its local direction of travel.
 * Positive `metres` moves it to the right-hand side of the direction of travel.
 *
 * Used to fan out driver routes that share the same roads: each route is nudged a few
 * pixels off the others so a stack of overlapping lines reads as parallel lanes instead
 * of a single hidden line. The offset is computed from the local heading at each vertex
 * (averaged across the adjacent segments) so corners stay smooth.
 */
export function offsetPath(path: LatLng[], metres: number): LatLng[] {
  if (metres === 0 || path.length < 2) return path;
  return path.map((pt, i) => {
    const prev = path[Math.max(0, i - 1)];
    const next = path[Math.min(path.length - 1, i + 1)];
    const latRad = (pt.lat * Math.PI) / 180;
    // Local heading as a metre-space vector (east = +x, north = +y).
    const dEast = ((next.lng - prev.lng) * Math.PI) / 180 * Math.cos(latRad) * EARTH_RADIUS_M;
    const dNorth = ((next.lat - prev.lat) * Math.PI) / 180 * EARTH_RADIUS_M;
    const len = Math.hypot(dEast, dNorth) || 1;
    // Right-hand perpendicular of the heading, normalised.
    const perpEast = dNorth / len;
    const perpNorth = -dEast / len;
    const offEast = perpEast * metres;
    const offNorth = perpNorth * metres;
    return {
      lat: pt.lat + (offNorth / EARTH_RADIUS_M) * (180 / Math.PI),
      lng: pt.lng + (offEast / (EARTH_RADIUS_M * Math.cos(latRad))) * (180 / Math.PI),
    };
  });
}
