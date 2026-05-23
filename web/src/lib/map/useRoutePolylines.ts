"use client";

import { useEffect, useMemo, useRef, useState } from "react";
import { useMapsLibrary } from "@vis.gl/react-google-maps";
import type { LatLng, SolutionRoute } from "@/lib/api/schema";

/**
 * Fetches road-snapped polylines for each driver route via Google's modern
 * Routes API (`Route.computeRoutes`, the successor to the deprecated
 * `DirectionsService.route`). Called through the Maps JS SDK so the call
 * goes via the API key configured for the loaded script — the REST endpoint
 * doesn't allow CORS.
 *
 * Falls back to `null` per-route on failure so callers can render the
 * straight-line `route.polyline` as a fallback. Results are cached by a
 * driver+waypoint hash so route reorderings don't invalidate caches and
 * per-render lookups stay cheap. Must be called inside an `<APIProvider>`
 * tree.
 */
export function useRoutePolylines(
  routes: SolutionRoute[],
  destination: LatLng,
  driverOrigins: Record<string, LatLng>,
): Array<LatLng[] | null> {
  const routesLibrary = useMapsLibrary("routes");
  const [byKey, setByKey] = useState<Record<string, LatLng[]>>({});

  // The cache key for a single route encodes everything that would change
  // its snapped path. Used both to dedupe in-flight fetches and to read from
  // `byKey` on the return path.
  const keyFor = (r: SolutionRoute): string => {
    const o = driverOrigins[r.driverParticipantId];
    return JSON.stringify({
      id: r.driverParticipantId,
      o,
      s: r.stops.map((s) => s.location),
      d: destination,
    });
  };

  const requestedRoutes = useMemo(() => {
    return routes
      .filter((r) => driverOrigins[r.driverParticipantId])
      .map((r) => ({ key: keyFor(r), route: r }));
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [routes, destination, driverOrigins]);

  const cancelledRef = useRef(false);

  useEffect(() => {
    cancelledRef.current = false;
    if (!routesLibrary) return;
    const toFetch = requestedRoutes.filter(({ key }) => !(key in byKey));
    if (toFetch.length === 0) return;

    Promise.all(
      toFetch.map(async ({ key, route }) => {
        const origin = driverOrigins[route.driverParticipantId];
        try {
          // computeRoutes requires an explicit field mask — we only need the
          // path to draw the snapped polyline. Avoid `["*"]` per the SDK's
          // performance guidance.
          const result = await routesLibrary.Route.computeRoutes({
            origin: { lat: origin.lat, lng: origin.lng },
            destination: { lat: destination.lat, lng: destination.lng },
            intermediates: route.stops.map((s) => ({
              location: { lat: s.location.lat, lng: s.location.lng },
            })),
            travelMode: "DRIVING",
            fields: ["path"],
          });
          const path = result.routes?.[0]?.path;
          if (!path || path.length === 0) return [key, null] as const;
          // LatLngAltitude exposes lat/lng as getter properties (vs. the
          // legacy LatLng's .lat() / .lng() methods).
          const decoded: LatLng[] = path.map((p) => ({ lat: p.lat, lng: p.lng }));
          return [key, decoded] as const;
        } catch {
          return [key, null] as const;
        }
      }),
    ).then((results) => {
      if (cancelledRef.current) return;
      setByKey((prev) => {
        const next = { ...prev };
        for (const [k, v] of results) {
          if (v) next[k] = v;
        }
        return next;
      });
    });

    return () => {
      cancelledRef.current = true;
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [routesLibrary, requestedRoutes]);

  return routes.map((r) => byKey[keyFor(r)] ?? null);
}
