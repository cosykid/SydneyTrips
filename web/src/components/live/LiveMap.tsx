"use client";

// Lightweight map for the driver/passenger live views. Shares the Mapbox token
// + style with PlanMap but only renders the locked solution's routes, the
// stops, the destination, and a live driver-position dot.

import { useMemo } from "react";
import MapboxMap, { Layer, Source, Marker } from "react-map-gl/mapbox";
import "mapbox-gl/dist/mapbox-gl.css";
import { CarFront } from "lucide-react";
import type { LatLng, SolutionRoute, SolutionStop, Solution } from "@/lib/api/schema";
import { MapFallback } from "@/components/map/MapFallback";
import type { MapViewState } from "@/lib/store";

export interface LiveMapProps {
  destination: { address: string; point: LatLng };
  route?: SolutionRoute;
  /** All stops to show with passenger-arrived state. */
  stopsArrived?: Record<string, boolean>;
  /** Live driver position dot. */
  driverPosition?: LatLng;
  /** When set, highlight a single stop (e.g. the current passenger's pickup). */
  highlightStopIndex?: number;
  viewState: MapViewState;
  onMove: (next: MapViewState) => void;
  /** Optionally render the participant's own home as a small marker. */
  participantHome?: LatLng;
}

interface PointFeature {
  type: "Feature";
  geometry: { type: "Point"; coordinates: [number, number] };
  properties: Record<string, string | number | boolean>;
}

interface LineFeature {
  type: "Feature";
  geometry: { type: "LineString"; coordinates: Array<[number, number]> };
  properties: Record<string, string | number | boolean>;
}

interface FeatureCollection<F> {
  type: "FeatureCollection";
  features: F[];
}

function pt(loc: LatLng, props: Record<string, string | number | boolean>): PointFeature {
  return {
    type: "Feature",
    geometry: { type: "Point", coordinates: [loc.lng, loc.lat] },
    properties: props,
  };
}

export function LiveMap({
  destination,
  route,
  stopsArrived = {},
  driverPosition,
  highlightStopIndex,
  viewState,
  onMove,
  participantHome,
}: LiveMapProps): React.JSX.Element {
  const token = process.env.NEXT_PUBLIC_MAPBOX_TOKEN;

  const routeFc = useMemo<FeatureCollection<LineFeature>>(() => {
    if (!route) return { type: "FeatureCollection", features: [] };
    return {
      type: "FeatureCollection",
      features: [
        {
          type: "Feature",
          geometry: {
            type: "LineString",
            coordinates: route.polyline.map((p) => [p.lng, p.lat] as [number, number]),
          },
          properties: { colour: route.colour },
        },
      ],
    };
  }, [route]);

  const stopFc = useMemo<FeatureCollection<PointFeature>>(() => {
    if (!route) return { type: "FeatureCollection", features: [] };
    return {
      type: "FeatureCollection",
      features: route.stops.map((s: SolutionStop, idx) =>
        pt(s.location, {
          id: s.candidateNodeId ?? `stop-${idx}`,
          idx,
          highlighted: idx === highlightStopIndex,
          arrived: Boolean(stopsArrived[s.candidateNodeId ?? `stop-${idx}`]),
          passengerCount: s.passengerIds.length,
        }),
      ),
    };
  }, [route, highlightStopIndex, stopsArrived]);

  const destinationFc = useMemo<FeatureCollection<PointFeature>>(
    () => ({
      type: "FeatureCollection",
      features: [pt(destination.point, { label: destination.address })],
    }),
    [destination.address, destination.point],
  );

  const homeFc = useMemo<FeatureCollection<PointFeature>>(
    () =>
      participantHome
        ? {
            type: "FeatureCollection",
            features: [pt(participantHome, { kind: "home" })],
          }
        : { type: "FeatureCollection", features: [] },
    [participantHome],
  );

  if (!token) {
    // Wrap the route as a single-solution shape so MapFallback can render it.
    const wrappedSolution: Solution | undefined = route
      ? {
          id: "wrap",
          label: "current",
          metrics: {
            totalDrivingMinutes: route.drivingMinutes,
            maxDrivingMinutes: route.drivingMinutes,
            totalStops: route.stops.length,
            totalWalkMetres: 0,
            maxWalkMetres: 0,
            fairnessIndex: 0,
          },
          routes: [route],
        }
      : undefined;
    return (
      <MapFallback
        destination={destination}
        solution={wrappedSolution}
        driverPosition={driverPosition}
        participantHome={participantHome}
        highlightStopIndex={highlightStopIndex}
      />
    );
  }

  return (
    <MapboxMap
      mapboxAccessToken={token}
      mapStyle="mapbox://styles/mapbox/light-v11"
      latitude={viewState.latitude}
      longitude={viewState.longitude}
      zoom={viewState.zoom}
      onMove={(evt) =>
        onMove({
          latitude: evt.viewState.latitude,
          longitude: evt.viewState.longitude,
          zoom: evt.viewState.zoom,
        })
      }
      style={{ width: "100%", height: "100%" }}
    >
      <Source id="live-route" type="geojson" data={routeFc}>
        <Layer
          id="live-route-layer"
          type="line"
          paint={{
            "line-color": ["get", "colour"],
            "line-width": 5,
            "line-opacity": 0.8,
          }}
          layout={{ "line-cap": "round", "line-join": "round" }}
        />
      </Source>

      <Source id="live-stops" type="geojson" data={stopFc}>
        <Layer
          id="live-stops-layer"
          type="circle"
          paint={{
            "circle-radius": ["case", ["==", ["get", "highlighted"], true], 11, 7],
            "circle-color": [
              "case",
              ["==", ["get", "arrived"], true],
              "#10B981",
              ["==", ["get", "highlighted"], true],
              "#F59E0B",
              "#3B82F6",
            ],
            "circle-stroke-width": 2,
            "circle-stroke-color": "#1F2937",
          }}
        />
      </Source>

      <Source id="live-destination" type="geojson" data={destinationFc}>
        <Layer
          id="live-destination-layer"
          type="symbol"
          layout={{
            "text-field": "★",
            "text-size": 28,
            "text-allow-overlap": true,
          }}
          paint={{
            "text-color": "#111827",
            "text-halo-color": "#FBBF24",
            "text-halo-width": 1.6,
          }}
        />
      </Source>

      {participantHome ? (
        <Source id="live-home" type="geojson" data={homeFc}>
          <Layer
            id="live-home-layer"
            type="circle"
            paint={{
              "circle-radius": 5,
              "circle-color": "#DC2626",
              "circle-stroke-width": 1.5,
              "circle-stroke-color": "#7F1D1D",
            }}
          />
        </Source>
      ) : null}

      {driverPosition ? (
        <Marker
          longitude={driverPosition.lng}
          latitude={driverPosition.lat}
          anchor="center"
        >
          <div
            className="bg-primary text-primary-foreground flex h-7 w-7 items-center justify-center rounded-full shadow-lg ring-2 ring-white"
            data-testid="driver-dot"
            aria-label="Driver position"
          >
            <CarFront className="h-3.5 w-3.5" />
          </div>
        </Marker>
      ) : null}
    </MapboxMap>
  );
}

// Helper: pick a sensible centre/zoom for a given collection of points so the
// caller can seed `viewState` without doing the maths.
export function fitBounds(
  points: LatLng[],
  fallback: MapViewState,
  padding = 0.04,
): MapViewState {
  if (points.length === 0) return fallback;
  const lats = points.map((p) => p.lat);
  const lngs = points.map((p) => p.lng);
  const minLat = Math.min(...lats) - padding;
  const maxLat = Math.max(...lats) + padding;
  const minLng = Math.min(...lngs) - padding;
  const maxLng = Math.max(...lngs) + padding;
  const span = Math.max(maxLat - minLat, maxLng - minLng);
  let zoom = 11;
  if (span < 0.05) zoom = 13;
  else if (span < 0.15) zoom = 12;
  else if (span > 0.4) zoom = 10;
  return {
    latitude: (minLat + maxLat) / 2,
    longitude: (minLng + maxLng) / 2,
    zoom,
  };
}
