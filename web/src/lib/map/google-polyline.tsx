"use client";

import { useEffect } from "react";
import { useMap } from "@vis.gl/react-google-maps";
import type { LatLng } from "@/lib/api/schema";

interface GooglePolylineProps {
  path: LatLng[];
  color: string;
  weight?: number;
  opacity?: number;
  /**
   * When true, renders the polyline as a dashed line (transparent stroke +
   * repeating dot symbols) — used to distinguish a passenger's walk / public-
   * transport leg from the driver's car route.
   */
  dashed?: boolean;
  /**
   * When true, repeats a forward-pointing arrowhead along the line so the
   * direction of travel is readable at a glance.
   */
  arrows?: boolean;
  /**
   * When true, draws a wider white line directly beneath the coloured stroke.
   * The halo keeps overlapping/crossing routes legible against each other and
   * against the basemap (the same trick Google's own directions layer uses).
   */
  casing?: boolean;
  /** Stacking order; higher draws on top. The casing always sits just below its line. */
  zIndex?: number;
}

/**
 * Declarative wrapper around `google.maps.Polyline`. The @vis.gl/react-google-maps
 * package doesn't ship a Polyline React component, so we create / dispose the
 * imperative one via the `useMap()` instance.
 */
export function GooglePolyline({
  path,
  color,
  weight = 5,
  opacity = 0.9,
  dashed = false,
  arrows = false,
  casing = false,
  zIndex,
}: GooglePolylineProps): null {
  const map = useMap();

  useEffect(() => {
    if (!map || path.length < 2) return;
    const coords = path.map((p) => ({ lat: p.lat, lng: p.lng }));
    const overlays: google.maps.Polyline[] = [];

    // White halo beneath the coloured stroke — drawn first / lower so the colour sits on top.
    if (casing) {
      overlays.push(
        new google.maps.Polyline({
          path: coords,
          geodesic: false,
          strokeColor: "#FFFFFF",
          strokeOpacity: dashed ? 0 : 0.95,
          strokeWeight: weight + 3,
          zIndex: zIndex === undefined ? undefined : zIndex - 1,
          map,
        }),
      );
    }

    // Repeating symbols layered on the line: dash dots and/or direction arrows.
    const icons: google.maps.IconSequence[] = [];
    if (dashed) {
      // For dashed lines the stroke itself is invisible — these dots do the rendering.
      // Spacing the dots ~ every 3× the line weight reads as a balanced dash pattern.
      icons.push({
        icon: {
          path: "M 0,-1 0,1",
          strokeColor: color,
          strokeOpacity: opacity,
          strokeWeight: weight,
          scale: 2,
        },
        offset: "0",
        repeat: `${Math.max(weight * 3, 12)}px`,
      });
    }
    if (arrows) {
      icons.push({
        icon: {
          path: google.maps.SymbolPath.FORWARD_CLOSED_ARROW,
          fillColor: "#FFFFFF",
          fillOpacity: 1,
          strokeColor: color,
          strokeOpacity: 1,
          strokeWeight: 1,
          scale: Math.max(weight * 0.55, 2.5),
        },
        offset: "24px",
        repeat: "110px",
      });
    }

    overlays.push(
      new google.maps.Polyline({
        path: coords,
        geodesic: false,
        strokeColor: color,
        strokeOpacity: dashed ? 0 : opacity,
        strokeWeight: weight,
        icons: icons.length ? icons : undefined,
        zIndex,
        map,
      }),
    );

    return () => {
      for (const o of overlays) o.setMap(null);
    };
  }, [map, path, color, weight, opacity, dashed, arrows, casing, zIndex]);

  return null;
}
