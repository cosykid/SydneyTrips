"use client";

import { useMemo, useState } from "react";
import {
  APIProvider,
  AdvancedMarker,
  InfoWindow,
  Map,
  Pin,
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

/** Fixed colours for the two non-car modes so the user can read mode at a glance,
 *  independent of which driver is doing the pickup. */
const WALK_COLOUR = "#475569"; // slate-600
const PT_COLOUR = "#7C3AED"; // violet-600 — distinct from any driver palette entry

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

interface PassengerLeg {
  participantId: string;
  origin: LatLng;
  pickup: LatLng;
  /** Walking minutes from home to pickup (always the walking portion, even when PT is involved). */
  walkMins: number;
  /** Public-transport minutes from home to pickup (0 when the passenger walks the whole way). */
  ptMins: number;
  /** Real PT geometry from home to pickup, when the backend provided it (multi-leg polyline
   *  flattened to a single LatLng sequence). When present, the renderer draws this instead of
   *  a straight crow-fly line. Undefined for walk-only legs and legacy nodes. */
  path?: LatLng[];
  /** The driver picking them up — used in the hover detail panel, not in the leg's visual style. */
  driverColour: string;
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

  /** Lookup: (participantId, candidateNodeId) → PT polyline path. Candidate nodes carry their
   *  own home→hub geometry; we key by participant + candidate so a passenger's "Home" candidate
   *  (no path) doesn't collide with their "Epping" candidate. */
  const pathByPassengerNode = useMemo<Record<string, LatLng[]>>(() => {
    const out: Record<string, LatLng[]> = {};
    for (const n of candidateNodes) {
      if (!n.participantId || !n.path || n.path.length < 2) continue;
      out[`${n.participantId}::${n.id}`] = n.path;
    }
    return out;
  }, [candidateNodes]);

  /** For each passenger, the pickup stop they've been assigned to, with walk/PT minutes coming
   *  from the new SolutionStop.pickupLegs payload — those drive the visual style below. */
  const passengerLegs = useMemo<PassengerLeg[]>(() => {
    if (!solution) return [];
    const byId: Record<string, Participant> = {};
    for (const p of participants) byId[p.id] = p;
    const legs: PassengerLeg[] = [];
    solution.routes.forEach((route, idx) => {
      const colour = route.colour ?? driverColour(idx);
      for (const stop of route.stops) {
        for (const leg of stop.pickupLegs) {
          const p = byId[leg.participantId];
          if (!p) continue;
          const pathKey = stop.candidateNodeId
            ? `${leg.participantId}::${stop.candidateNodeId}`
            : null;
          legs.push({
            participantId: leg.participantId,
            origin: p.origin,
            pickup: stop.location,
            walkMins: leg.walkMins,
            ptMins: leg.ptMins,
            path: pathKey ? pathByPassengerNode[pathKey] : undefined,
            driverColour: colour,
          });
        }
      }
    });
    return legs;
  }, [solution, participants, pathByPassengerNode]);

  /** Driver id → route colour so each driver's home marker carries the same colour as their road
   *  line. Passenger markers stay neutral (modes encode their leg). */
  const driverColourById = useMemo<Record<string, string>>(() => {
    if (!solution) return {};
    const out: Record<string, string> = {};
    solution.routes.forEach((route, idx) => {
      out[route.driverParticipantId] = route.colour ?? driverColour(idx);
    });
    return out;
  }, [solution]);

  const [hoveredId, setHoveredId] = useState<string | null>(null);
  const hovered = hoveredId
    ? participants.find((p) => p.id === hoveredId) ?? null
    : null;
  const hoveredLeg = hoveredId
    ? passengerLegs.find((l) => l.participantId === hoveredId) ?? null
    : null;

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

      {/* Driver (car) routes — solid road-snapped polyline; falls back to the
          straight `route.polyline` while the Routes API request is in flight. */}
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

      {/* Passenger home → pickup legs. Three visual styles, in priority order:
            1. PT with a real polyline (path present):  single dashed violet line following the
                                                         actual TfNSW journey (walks + PT legs
                                                         concatenated).
            2. PT but no polyline (legacy node, stub):   schematic walk/PT split along a straight
                                                         line — the old behaviour, kept as a
                                                         fallback so the map doesn't go blank
                                                         on data generated before this feature.
            3. Walk-only (ptMins == 0):                  single dashed slate line from home to
                                                         pickup. */}
      {passengerLegs.flatMap((leg) => {
        if (leg.ptMins <= 0) {
          return [
            <GooglePolyline
              key={`walk-${leg.participantId}`}
              path={[leg.origin, leg.pickup]}
              color={WALK_COLOUR}
              weight={3}
              opacity={0.85}
              dashed
            />,
          ];
        }
        if (leg.path && leg.path.length >= 2) {
          return [
            <GooglePolyline
              key={`pt-${leg.participantId}`}
              path={leg.path}
              color={PT_COLOUR}
              weight={4}
              opacity={0.9}
              dashed
            />,
          ];
        }
        // Fallback: no polyline available — schematic split.
        const split = computeSplitPoint(leg.origin, leg.pickup, leg.walkMins, leg.ptMins);
        return [
          <GooglePolyline
            key={`walk-${leg.participantId}`}
            path={[leg.origin, split]}
            color={WALK_COLOUR}
            weight={3}
            opacity={0.85}
            dashed
          />,
          <GooglePolyline
            key={`pt-${leg.participantId}`}
            path={[split, leg.pickup]}
            color={PT_COLOUR}
            weight={4}
            opacity={0.9}
            dashed
          />,
        ];
      })}

      {/* Midpoint label per leg: minute breakdown so a quick glance answers
          "is this person walking, or riding PT?". */}
      {passengerLegs.map((leg) => {
        const mid = midpoint(leg.origin, leg.pickup);
        const label =
          leg.ptMins > 0
            ? `${leg.walkMins}+${leg.ptMins} min`
            : leg.walkMins > 0
              ? `${leg.walkMins} min walk`
              : "";
        if (!label) return null;
        return (
          <AdvancedMarker
            key={`leg-label-${leg.participantId}`}
            position={{ lat: mid.lat, lng: mid.lng }}
          >
            <div
              className="rounded-full bg-white/95 px-1.5 py-0.5 text-[10px] font-medium shadow ring-1 ring-slate-300"
              style={{
                color: leg.ptMins > 0 ? PT_COLOUR : WALK_COLOUR,
                whiteSpace: "nowrap",
                lineHeight: 1.1,
              }}
            >
              {leg.ptMins > 0 ? "🚆 " : "🚶 "}
              {label}
            </div>
          </AdvancedMarker>
        );
      })}

      {/* Pickup stops — green pins. Hubs with a transit modality get an extra mode glyph so the
          map answers "is this a train/bus/ferry stop?" without needing the hover tooltip. */}
      {solution?.routes.flatMap((route) =>
        route.stops.map((s, sidx) => (
          <AdvancedMarker
            key={`stop-${route.driverParticipantId}-${sidx}`}
            position={{ lat: s.location.lat, lng: s.location.lng }}
          >
            <div className="flex flex-col items-center">
              {modalityIcon(s.nodeKind) ? (
                <span
                  className="mb-0.5 rounded-full bg-white/95 px-1 py-0.5 text-[11px] shadow ring-1 ring-slate-300"
                  style={{ lineHeight: 1 }}
                  aria-label={s.nodeKind}
                >
                  {modalityIcon(s.nodeKind)}
                </span>
              ) : null}
              <div className="h-3.5 w-3.5 rounded-full bg-[#34A853] ring-2 ring-white shadow-md" />
            </div>
          </AdvancedMarker>
        )),
      )}

      {/* Participant origins — name label above the dot, colour matches the
          driver they ride with (or their own route colour, for drivers). */}
      {participants.map((p) => {
        const colour =
          p.role === "driver"
            ? driverColourById[p.id] ?? "#1A73E8"
            : passengerLegs.find((l) => l.participantId === p.id)?.driverColour ??
              "#1A73E8";
        return (
          <AdvancedMarker
            key={`origin-${p.id}`}
            position={{ lat: p.origin.lat, lng: p.origin.lng }}
            onMouseEnter={() => setHoveredId(p.id)}
            onMouseLeave={() => setHoveredId((cur) => (cur === p.id ? null : cur))}
            onClick={() => setHoveredId(p.id)}
          >
            <div className="flex flex-col items-center pointer-events-auto">
              <span
                className="mb-1 max-w-[140px] truncate rounded-full bg-white/95 px-2 py-0.5 text-[11px] font-medium text-slate-800 shadow ring-1 ring-slate-300"
                style={{ lineHeight: 1.2 }}
              >
                {p.displayName}
              </span>
              <div
                className={
                  (p.role === "driver" ? "h-4 w-4" : "h-3 w-3") +
                  " rounded-full ring-2 ring-white shadow-md"
                }
                style={{ backgroundColor: colour }}
              />
            </div>
          </AdvancedMarker>
        );
      })}

      {/* Hover details for the focused participant. */}
      {hovered ? (
        <InfoWindow
          position={{ lat: hovered.origin.lat, lng: hovered.origin.lng }}
          pixelOffset={[0, -36]}
          onCloseClick={() => setHoveredId(null)}
          headerContent={
            <span className="text-sm font-semibold text-slate-900">
              {hovered.displayName}
            </span>
          }
        >
          <div className="space-y-1 text-xs text-slate-700">
            <div>
              <span className="font-medium capitalize">{hovered.role}</span>
              {hovered.role === "driver" && hovered.seatsAvailable
                ? ` · ${hovered.seatsAvailable} seats`
                : null}
            </div>
            <div className="text-slate-600">{hovered.originAddress}</div>
            {hovered.role === "passenger" && hoveredLeg ? (
              <div className="space-y-0.5 pt-1 text-slate-600">
                <div>
                  🚶 Walk to pickup:{" "}
                  <span className="font-medium text-slate-800">
                    {hoveredLeg.walkMins > 0 ? `${hoveredLeg.walkMins} min` : "—"}
                  </span>
                </div>
                {hoveredLeg.ptMins > 0 ? (
                  <div>
                    🚆 Public transport:{" "}
                    <span className="font-medium text-slate-800">
                      {hoveredLeg.ptMins} min
                    </span>
                  </div>
                ) : null}
              </div>
            ) : null}
            {hovered.role === "passenger" && !hoveredLeg ? (
              <div className="pt-1 italic text-slate-500">No pickup assigned yet</div>
            ) : null}
          </div>
        </InfoWindow>
      ) : null}

      {/* Destination — Google-Maps style red pin, anchored at the tip. */}
      <AdvancedMarker
        position={{ lat: destination.point.lat, lng: destination.point.lng }}
        title={destination.address}
      >
        <Pin background="#EA4335" borderColor="#000000" glyphColor="#000000" scale={1.2} />
      </AdvancedMarker>
    </Map>
  );
}

function midpoint(a: LatLng, b: LatLng): LatLng {
  return { lat: (a.lat + b.lat) / 2, lng: (a.lng + b.lng) / 2 };
}

/** Linearly interpolate along the home→pickup line at the walk/PT minute ratio so the dashed
 *  walk segment ends, and the dotted PT segment begins, at a point proportional to the walk
 *  share of the journey. Bounded to [10%, 40%] so the walk leg is always visible without
 *  swamping the PT leg. */
function computeSplitPoint(
  origin: LatLng,
  pickup: LatLng,
  walkMins: number,
  ptMins: number,
): LatLng {
  const total = walkMins + ptMins;
  let t = total > 0 ? walkMins / total : 0.2;
  if (t < 0.1) t = 0.1;
  if (t > 0.4) t = 0.4;
  return {
    lat: origin.lat + (pickup.lat - origin.lat) * t,
    lng: origin.lng + (pickup.lng - origin.lng) * t,
  };
}

function modalityIcon(modality: CandidateNode["modality"] | undefined): string | null {
  switch (modality) {
    case "train_station":
      return "🚆";
    case "bus_stop":
      return "🚌";
    case "ferry_wharf":
      return "⛴";
    case "light_rail":
      return "🚊";
    default:
      return null;
  }
}
