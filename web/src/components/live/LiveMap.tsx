"use client";

import { useMemo } from "react";
import {
  APIProvider,
  AdvancedMarker,
  Map,
} from "@vis.gl/react-google-maps";
import { CarFront } from "lucide-react";
import type { LatLng, SolutionRoute, Solution } from "@/lib/api/schema";
import { GooglePolyline } from "@/lib/map/google-polyline";
import { useRoutePolylines } from "@/lib/map/useRoutePolylines";
import { MapFallback } from "@/components/map/MapFallback";
import type { MapViewState } from "@/lib/store";

export interface LiveMapProps {
  destination: { address: string; point: LatLng };
  route?: SolutionRoute;
  /** Per-stop "arrived" flag. */
  stopsArrived?: Record<string, boolean>;
  /** Live driver position (live or simulated). */
  driverPosition?: LatLng;
  /** When set, highlight a single stop (e.g. the current passenger's pickup). */
  highlightStopIndex?: number;
  viewState: MapViewState;
  onMove: (next: MapViewState) => void;
  /** Optionally render the passenger's own home as a small marker. */
  participantHome?: LatLng;
}

const MAP_ID = process.env.NEXT_PUBLIC_GOOGLE_MAPS_MAP_ID ?? "DEMO_MAP_ID";

export function LiveMap(props: LiveMapProps): React.JSX.Element {
  const apiKey = process.env.NEXT_PUBLIC_GOOGLE_MAPS_KEY;
  if (!apiKey) {
    // Wrap the route as a single-solution shape so MapFallback can render it.
    const wrappedSolution: Solution | undefined = props.route
      ? {
          id: "wrap",
          label: "current",
          metrics: {
            totalDrivingMinutes: props.route.drivingMinutes,
            maxDrivingMinutes: props.route.drivingMinutes,
            totalStops: props.route.stops.length,
            totalWalkMetres: 0,
            maxWalkMetres: 0,
            fairnessIndex: 0,
          },
          routes: [props.route],
        }
      : undefined;
    return (
      <MapFallback
        destination={props.destination}
        solution={wrappedSolution}
        driverPosition={props.driverPosition}
        participantHome={props.participantHome}
        highlightStopIndex={props.highlightStopIndex}
      />
    );
  }
  return (
    <APIProvider apiKey={apiKey} libraries={["routes"]}>
      <LiveMapInner {...props} />
    </APIProvider>
  );
}

function LiveMapInner({
  destination,
  route,
  stopsArrived = {},
  driverPosition,
  highlightStopIndex,
  viewState,
  onMove,
  participantHome,
}: LiveMapProps): React.JSX.Element {
  // `route.polyline[0]` is the driver's origin (per `apiRouteToUi` in
  // adapters.ts) — reuse it directly rather than threading a separate prop.
  const driverOrigins = useMemo<Record<string, LatLng>>(() => {
    if (!route || !route.polyline.length) return {};
    return { [route.driverParticipantId]: route.polyline[0] };
  }, [route]);

  const snappedPolylines = useRoutePolylines(
    route ? [route] : [],
    destination.point,
    driverOrigins,
  );

  const path = route ? snappedPolylines[0] ?? route.polyline : null;

  return (
    <Map
      mapId={MAP_ID}
      center={{ lat: viewState.latitude, lng: viewState.longitude }}
      zoom={viewState.zoom}
      gestureHandling="greedy"
      disableDefaultUI={false}
      clickableIcons={false}
      onCameraChanged={(ev) => {
        const { center, zoom } = ev.detail;
        onMove({ latitude: center.lat, longitude: center.lng, zoom });
      }}
      style={{ width: "100%", height: "100%" }}
    >
      {route && path ? (
        <GooglePolyline path={path} color={route.colour ?? "#1A73E8"} weight={6} />
      ) : null}

      {route?.stops.map((s, idx) => {
        const key = s.candidateNodeId ?? `stop-${idx}`;
        const arrived = Boolean(stopsArrived[key]);
        const highlighted = idx === highlightStopIndex;
        const color = arrived ? "bg-[#34A853]" : highlighted ? "bg-[#FBBC04]" : "bg-[#1A73E8]";
        const size = highlighted ? "h-4 w-4" : "h-3 w-3";
        return (
          <AdvancedMarker
            key={`stop-${idx}`}
            position={{ lat: s.location.lat, lng: s.location.lng }}
          >
            <div
              className={`${size} ${color} rounded-full ring-2 ring-white shadow-md`}
              title={highlighted ? "Your pickup" : `Stop ${idx + 1}`}
            />
          </AdvancedMarker>
        );
      })}

      {participantHome ? (
        <AdvancedMarker position={{ lat: participantHome.lat, lng: participantHome.lng }}>
          <div className="h-3.5 w-3.5 rounded-full bg-[#EA4335] ring-2 ring-white shadow-md" />
        </AdvancedMarker>
      ) : null}

      {driverPosition ? (
        <AdvancedMarker
          position={{ lat: driverPosition.lat, lng: driverPosition.lng }}
          title="Driver"
        >
          <div
            className="bg-[#1A73E8] text-white flex h-8 w-8 items-center justify-center rounded-full shadow-lg ring-2 ring-white"
            data-testid="driver-dot"
            aria-label="Driver position"
          >
            <CarFront className="h-4 w-4" />
          </div>
        </AdvancedMarker>
      ) : null}

      <AdvancedMarker
        position={{ lat: destination.point.lat, lng: destination.point.lng }}
        title={destination.address}
      >
        <div className="text-3xl drop-shadow-md" style={{ textShadow: "0 0 8px #FBBC04" }}>
          ★
        </div>
      </AdvancedMarker>
    </Map>
  );
}

/** Pick a sensible centre/zoom for a given collection of points. */
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
