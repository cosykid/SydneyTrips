"use client";

import { useMemo } from "react";
import {
  APIProvider,
  AdvancedMarker,
  Map,
} from "@vis.gl/react-google-maps";
import type {
  CandidateNode,
  LatLng,
  Participant,
  Solution,
  TripSummary,
} from "@/lib/api/schema";
import { driverColour } from "@/lib/map/palette";
import { GooglePolyline } from "@/lib/map/google-polyline";
import { useRoutePolylines } from "@/lib/map/useRoutePolylines";
import { MapFallback } from "@/components/map/MapFallback";
import type { MapViewState } from "@/lib/store";

export interface PlanMapProps {
  destination: { address: string; point: LatLng };
  participants: Participant[];
  candidateNodes: CandidateNode[];
  solution?: Solution;
  trip?: Pick<TripSummary, "id" | "name">;
  viewState: MapViewState;
  onMove: (next: MapViewState) => void;
}

const MAP_ID = process.env.NEXT_PUBLIC_GOOGLE_MAPS_MAP_ID ?? "DEMO_MAP_ID";

export function PlanMap(props: PlanMapProps): React.JSX.Element {
  const apiKey = process.env.NEXT_PUBLIC_GOOGLE_MAPS_KEY;
  if (!apiKey) {
    return (
      <MapFallback
        destination={props.destination}
        participants={props.participants}
        candidateNodes={props.candidateNodes}
        solution={props.solution}
      />
    );
  }
  return (
    <APIProvider apiKey={apiKey} libraries={["routes"]}>
      <PlanMapInner {...props} />
    </APIProvider>
  );
}

function PlanMapInner({
  destination,
  participants,
  candidateNodes,
  solution,
  viewState,
  onMove,
}: PlanMapProps): React.JSX.Element {
  const driverOrigins = useMemo<Record<string, LatLng>>(() => {
    const out: Record<string, LatLng> = {};
    for (const p of participants) {
      if (p.role === "driver") out[p.id] = p.origin;
    }
    return out;
  }, [participants]);

  const snappedPolylines = useRoutePolylines(
    solution?.routes ?? [],
    destination.point,
    driverOrigins,
  );

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
      {/* Candidate pickup nodes — small grey dots */}
      {candidateNodes.map((n) => (
        <AdvancedMarker key={`cn-${n.id}`} position={{ lat: n.location.lat, lng: n.location.lng }}>
          <div className="h-1.5 w-1.5 rounded-full bg-slate-400 opacity-60 ring-1 ring-slate-600" />
        </AdvancedMarker>
      ))}

      {/* Driver routes — road-snapped polyline when available, straight fallback otherwise */}
      {solution?.routes.map((route, idx) => {
        const path = snappedPolylines[idx] ?? route.polyline;
        return (
          <GooglePolyline
            key={`route-${route.driverParticipantId}`}
            path={path}
            color={route.colour ?? driverColour(idx)}
          />
        );
      })}

      {/* Pickup stops — green pins. */}
      {solution?.routes.flatMap((route) =>
        route.stops.map((s, sidx) => (
          <AdvancedMarker
            key={`stop-${route.driverParticipantId}-${sidx}`}
            position={{ lat: s.location.lat, lng: s.location.lng }}
          >
            <div className="h-3.5 w-3.5 rounded-full bg-[#34A853] ring-2 ring-white shadow-md" />
          </AdvancedMarker>
        )),
      )}

      {/* Participant origins — Maps-blue dots, larger for drivers */}
      {participants.map((p) => (
        <AdvancedMarker key={`origin-${p.id}`} position={{ lat: p.origin.lat, lng: p.origin.lng }}>
          <div
            className={
              p.role === "driver"
                ? "h-4 w-4 rounded-full bg-[#1A73E8] ring-2 ring-white shadow-md"
                : "h-3 w-3 rounded-full bg-[#1A73E8] ring-2 ring-white shadow-md"
            }
            title={p.displayName}
          />
        </AdvancedMarker>
      ))}

      {/* Destination — yellow star pin */}
      <AdvancedMarker
        position={{ lat: destination.point.lat, lng: destination.point.lng }}
        title={destination.address}
      >
        <div className="text-2xl drop-shadow-md" style={{ textShadow: "0 0 6px #FBBC04" }}>
          ★
        </div>
      </AdvancedMarker>
    </Map>
  );
}
