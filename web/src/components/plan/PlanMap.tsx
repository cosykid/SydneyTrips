"use client";

import { useMemo, useState } from "react";
import {
  APIProvider,
  AdvancedMarker,
  ControlPosition,
  InfoWindow,
  Map,
  Pin,
} from "@vis.gl/react-google-maps";
import type {
  CandidateNode,
  LatLng,
  Participant,
  PathLeg,
  Solution,
  TripSummary,
} from "@/lib/api/schema";
import { driverColour } from "@/lib/map/palette";
import { GooglePolyline } from "@/lib/map/google-polyline";
import { useRoutePolylines } from "@/lib/map/useRoutePolylines";
import { metresPerPixel, offsetPath } from "@/lib/map/offset";
import { PT_FALLBACK_COLOUR, WALK_COLOUR, legStyle, transitStyle } from "@/lib/map/transit";
import { TransitBadge } from "@/components/map/TransitBadge";
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

/** Pixel gap between adjacent parallel driver routes — converted to metres at the current
 *  zoom so routes that share roads fan out into legible parallel lanes instead of hiding
 *  one another. */
const ROUTE_FAN_PX = 6;

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
  /** Transit mode of the pickup hub, so the leg can be coloured/labelled by mode (TfNSW palette). */
  mode: CandidateNode["modality"];
  /** Per-segment journey (walk + each PT leg) with its own mode, so the renderer can colour each
   *  segment distinctly (Google-Maps style). Preferred over `path` when present. */
  pathLegs?: PathLeg[];
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

  // Fan overlapping driver routes apart. The offset is in metres but derived from a fixed
  // pixel gap at the current zoom, so the separation stays visually constant as you zoom.
  // Recomputed only when the zoom step changes or a snapped path resolves (its length appears),
  // keeping polyline identities — and therefore the underlying google.maps overlays — stable.
  const snappedSig = (solution?.routes ?? [])
    .map((_, i) => snappedPolylines[i]?.length ?? 0)
    .join(",");
  const zoomStep = Math.round(viewState.zoom);
  const offsetRoutePaths = useMemo<LatLng[][]>(() => {
    const routes = solution?.routes ?? [];
    const n = routes.length;
    if (n === 0) return [];
    const mpp = metresPerPixel(zoomStep, destination.point.lat);
    return routes.map((route, idx) => {
      const base = snappedPolylines[idx] ?? route.polyline;
      if (n < 2) return base;
      const metres = (idx - (n - 1) / 2) * ROUTE_FAN_PX * mpp;
      return offsetPath(base, metres);
    });
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [solution, snappedSig, zoomStep, destination.point.lat]);

  /** For each passenger, the pickup stop they've been assigned to, with walk/PT minutes and the
   *  home→pickup geometry coming straight from the SolutionStop.pickupLegs payload. The path is
   *  carried per-leg (not looked up by the stop's canonical candidate node) so co-located
   *  passengers each render along their own route instead of a shared straight line. */
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
          legs.push({
            participantId: leg.participantId,
            origin: p.origin,
            pickup: stop.location,
            walkMins: leg.walkMins,
            ptMins: leg.ptMins,
            mode: stop.nodeKind,
            pathLegs: leg.pathLegs && leg.pathLegs.length > 0 ? leg.pathLegs : undefined,
            path: leg.path && leg.path.length >= 2 ? leg.path : undefined,
            driverColour: colour,
          });
        }
      }
    });
    return legs;
  }, [solution, participants]);

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
      // The planner panel sits over the top-left corner, so push Google's
      // Map/Satellite toggle to the right edge where it stays reachable.
      mapTypeControlOptions={{ position: ControlPosition.TOP_RIGHT }}
      fullscreenControlOptions={{ position: ControlPosition.TOP_RIGHT }}
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

      {/* Driver (car) routes — solid road-snapped polyline, fanned apart when they share roads,
          with a white casing for crossing legibility and forward arrows for direction. Falls
          back to the straight `route.polyline` while the Routes API request is in flight. */}
      {solution?.routes.map((route, idx) => (
        <GooglePolyline
          key={`route-${route.driverParticipantId}`}
          path={offsetRoutePaths[idx] ?? route.polyline}
          color={route.colour ?? driverColour(idx)}
          weight={5}
          casing
          // Space z-indices by 2 so each route's white casing (zIndex-1) never lands on a
          // neighbour's coloured line where the fanned-out lanes brush against each other.
          zIndex={10 + idx * 2}
        />
      ))}

      {/* Passenger home → pickup legs. PT legs are coloured by the hub's TfNSW mode (train =
          orange, bus = blue, ferry = green, light rail = red); walks stay slate. Three visual
          styles, in priority order:
            1. PT with a real polyline (path present):  single dashed mode-coloured line following
                                                         the actual TfNSW journey.
            2. PT but no polyline (legacy node, stub):   schematic walk/PT split along a straight
                                                         line — the old behaviour, kept as a
                                                         fallback so the map doesn't go blank
                                                         on data generated before this feature.
            3. Walk-only (ptMins == 0):                  single dashed slate line from home to
                                                         pickup. */}
      {passengerLegs.flatMap((leg) => {
        // Preferred: colour each segment of the real multi-leg journey by its own mode
        // (walk = slate, then the TfNSW colour for each PT leg) — the Google-Maps look.
        if (leg.pathLegs && leg.pathLegs.length > 0) {
          return leg.pathLegs
            .filter((seg) => seg.path.length >= 2)
            .map((seg, i) => {
              const style = legStyle(seg.mode);
              return (
                <GooglePolyline
                  key={`segleg-${leg.participantId}-${i}`}
                  path={seg.path}
                  color={style.color}
                  weight={style.isWalk ? 3 : 4}
                  opacity={style.isWalk ? 0.85 : 0.9}
                  dashed
                />
              );
            });
        }
        const ptColour = transitStyle(leg.mode)?.color ?? PT_FALLBACK_COLOUR;
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
              color={ptColour}
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
            color={ptColour}
            weight={4}
            opacity={0.9}
            dashed
          />,
        ];
      })}

      {/* Midpoint label per leg: a TfNSW mode chip + minute breakdown so a glance answers both
          "is this person walking or riding PT?" and "which mode?". */}
      {passengerLegs.map((leg) => {
        if (leg.ptMins <= 0 && leg.walkMins <= 0) return null;
        // Anchor on the middle of the real PT path when we have it, else the crow-fly midpoint.
        const mid =
          leg.path && leg.path.length >= 2
            ? leg.path[Math.floor(leg.path.length / 2)]
            : midpoint(leg.origin, leg.pickup);
        const style = transitStyle(leg.mode);
        return (
          <AdvancedMarker
            key={`leg-label-${leg.participantId}`}
            position={{ lat: mid.lat, lng: mid.lng }}
          >
            <div
              className="flex items-center gap-1 rounded-full bg-white/95 px-1.5 py-0.5 text-[10px] font-medium shadow ring-1 ring-slate-300"
              style={{ whiteSpace: "nowrap", lineHeight: 1.1 }}
            >
              {leg.ptMins > 0 ? (
                <>
                  {style ? <TransitBadge modality={leg.mode} size={13} /> : <span>🚆</span>}
                  <span style={{ color: style?.color ?? PT_FALLBACK_COLOUR }}>
                    {leg.walkMins > 0
                      ? `${leg.walkMins} min walk · ${leg.ptMins} min`
                      : `${leg.ptMins} min`}
                  </span>
                </>
              ) : (
                <span style={{ color: WALK_COLOUR }}>🚶 {leg.walkMins} min walk</span>
              )}
            </div>
          </AdvancedMarker>
        );
      })}

      {/* Pickup stops — green pins. Transit hubs get a TfNSW mode chip above the dot so the map
          answers "is this a train/bus/ferry/light-rail stop?" without needing the hover tooltip. */}
      {solution?.routes.flatMap((route) =>
        route.stops.map((s, sidx) => (
          <AdvancedMarker
            key={`stop-${route.driverParticipantId}-${sidx}`}
            position={{ lat: s.location.lat, lng: s.location.lng }}
          >
            <div className="flex flex-col items-center">
              {transitStyle(s.nodeKind) ? (
                <span className="mb-0.5">
                  <TransitBadge modality={s.nodeKind} size={16} />
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
            ? driverColourById[p.id] ?? "#0E7C86"
            : passengerLegs.find((l) => l.participantId === p.id)?.driverColour ??
              "#0E7C86";
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
                  <div className="flex items-center gap-1">
                    {transitStyle(hoveredLeg.mode) ? (
                      <TransitBadge modality={hoveredLeg.mode} size={14} />
                    ) : (
                      <span>🚆</span>
                    )}
                    <span>
                      {transitStyle(hoveredLeg.mode)?.name ?? "Public transport"}:
                    </span>
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
