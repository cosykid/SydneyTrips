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
}: GooglePolylineProps): null {
  const map = useMap();

  useEffect(() => {
    if (!map || path.length < 2) return;
    const polyline = new google.maps.Polyline({
      path: path.map((p) => ({ lat: p.lat, lng: p.lng })),
      geodesic: false,
      strokeColor: color,
      // For dashed lines the stroke itself is invisible — the dot icons below
      // do the rendering. Spacing the dots ~ every 2× the line weight reads as
      // a balanced dash pattern at typical city zooms.
      strokeOpacity: dashed ? 0 : opacity,
      strokeWeight: weight,
      icons: dashed
        ? [
            {
              icon: {
                path: "M 0,-1 0,1",
                strokeColor: color,
                strokeOpacity: opacity,
                strokeWeight: weight,
                scale: 2,
              },
              offset: "0",
              repeat: `${Math.max(weight * 3, 12)}px`,
            },
          ]
        : undefined,
      map,
    });
    return () => {
      polyline.setMap(null);
    };
  }, [map, path, color, weight, opacity, dashed]);

  return null;
}
