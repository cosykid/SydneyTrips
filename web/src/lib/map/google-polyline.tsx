"use client";

import { useEffect } from "react";
import { useMap } from "@vis.gl/react-google-maps";
import type { LatLng } from "@/lib/api/schema";

interface GooglePolylineProps {
  path: LatLng[];
  color: string;
  weight?: number;
  opacity?: number;
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
}: GooglePolylineProps): null {
  const map = useMap();

  useEffect(() => {
    if (!map || path.length < 2) return;
    const polyline = new google.maps.Polyline({
      path: path.map((p) => ({ lat: p.lat, lng: p.lng })),
      geodesic: false,
      strokeColor: color,
      strokeOpacity: opacity,
      strokeWeight: weight,
      map,
    });
    return () => {
      polyline.setMap(null);
    };
  }, [map, path, color, weight, opacity]);

  return null;
}
