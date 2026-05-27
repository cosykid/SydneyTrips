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
  SolutionRoute,
  TripSummary,
} from "@/lib/api/schema";
import { driverColour } from "@/lib/map/palette";
import { GooglePolyline } from "@/lib/map/google-polyline";
import { useRoutePolylines } from "@/lib/map/useRoutePolylines";
import { metresPerPixel, offsetPath } from "@/lib/map/offset";
import {
  PT_FALLBACK_COLOUR,
  WALK_COLOUR,
  legMark,
  legModeName,
  legStyle,
  transitStyle,
} from "@/lib/map/transit";
import { TransitBadge } from "@/components/map/TransitBadge";
import { MapFallback } from "@/components/map/MapFallback";
import type { MapViewState } from "@/lib/store";

export interface PlanMapProps {
  destination: { address: string; point: LatLng };
  participants: Participant[];
  candidateNodes: CandidateNode[];
  solution?: Solution;
  trip?: Pick<TripSummary, "id" | "name" | "arriveBy">;
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
  /** Display name of the driver picking them up, shown at the foot of the hover itinerary. */
  driverName: string;
}

function PlanMapInner({
  destination,
  participants,
  candidateNodes,
  solution,
  trip,
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
            driverName: route.driverDisplayName,
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

  const participantsById = useMemo<Record<string, Participant>>(() => {
    const out: Record<string, Participant> = {};
    for (const p of participants) out[p.id] = p;
    return out;
  }, [participants]);

  const [hoveredId, setHoveredId] = useState<string | null>(null);
  const hovered = hoveredId ? participantsById[hoveredId] ?? null : null;
  const hoveredLeg = hoveredId
    ? passengerLegs.find((l) => l.participantId === hoveredId) ?? null
    : null;
  const hoveredDriverRoute =
    hovered?.role === "driver"
      ? solution?.routes.find((r) => r.driverParticipantId === hovered.id) ?? null
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

      {/* Per-leg minute labels used to float over the map midpoints, but they were unanchored and
          confusing ("17 min walk · 21 min" with no context). The full timed breakdown now lives in
          the per-person hover itinerary (see the InfoWindow below) instead. */}

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
              <JourneyDetail leg={hoveredLeg} />
            ) : null}
            {hovered.role === "passenger" && !hoveredLeg ? (
              <div className="pt-1 italic text-slate-500">No pickup assigned yet</div>
            ) : null}
            {hovered.role === "driver" && hoveredDriverRoute ? (
              <DriverDetail
                route={hoveredDriverRoute}
                destinationAddress={destination.address}
                arriveBy={trip?.arriveBy}
                participantsById={participantsById}
              />
            ) : null}
            {hovered.role === "driver" && !hoveredDriverRoute ? (
              <div className="pt-1 italic text-slate-500">No route assigned yet</div>
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

/** Format an ISO instant as a short Sydney clock time ("8:42 am"). The trip is always in Sydney, so
 *  we pin the zone rather than using the viewer's locale — otherwise an instant like 22:45Z (8:45am
 *  AEST) renders as the previous evening for anyone whose browser isn't on Sydney time. Null for
 *  missing/invalid input so the itinerary can simply omit the time gutter for stub / pre-feature
 *  legs. */
function formatClock(iso?: string): string | null {
  if (!iso) return null;
  const d = new Date(iso);
  if (Number.isNaN(d.getTime())) return null;
  return d.toLocaleTimeString("en-AU", {
    hour: "numeric",
    minute: "2-digit",
    timeZone: "Australia/Sydney",
  });
}

/** Pedestrian glyph (Material "directions_walk"), replacing the 🚶 emoji which rendered
 *  inconsistently across platforms. Inherits `currentColor`; size via the `size` prop. */
function WalkIcon({ size = 13, className }: { size?: number; className?: string }): React.JSX.Element {
  return (
    <svg
      viewBox="0 0 24 24"
      width={size}
      height={size}
      fill="currentColor"
      aria-hidden
      className={className}
    >
      <path d="M13.5 5.5c1.1 0 2-.9 2-2s-.9-2-2-2-2 .9-2 2 .9 2 2 2zM9.8 8.9 7 23h2.1l1.8-8 2.1 2v6h2v-7.5l-2.1-2 .6-3C14.8 12 16.8 13 19 13v-2c-1.7 0-3.2-.9-4-2.3l-1-1.6c-.4-.6-1-1-1.7-1-.3 0-.5.1-.8.1L6 8.3V13h2V9.6z" />
    </svg>
  );
}

/** Car glyph (Material "directions_car") for the driver's driving legs. Inherits `currentColor`. */
function CarIcon({ size = 13, className }: { size?: number; className?: string }): React.JSX.Element {
  return (
    <svg
      viewBox="0 0 24 24"
      width={size}
      height={size}
      fill="currentColor"
      aria-hidden
      className={className}
    >
      <path d="M18.92 6.01C18.72 5.42 18.16 5 17.5 5h-11c-.66 0-1.21.42-1.42 1.01L3 12v8c0 .55.45 1 1 1h1c.55 0 1-.45 1-1v-1h12v1c0 .55.45 1 1 1h1c.55 0 1-.45 1-1v-8l-2.08-5.99zM6.5 16c-.83 0-1.5-.67-1.5-1.5S5.67 13 6.5 13s1.5.67 1.5 1.5S7.33 16 6.5 16zm11 0c-.83 0-1.5-.67-1.5-1.5s.67-1.5 1.5-1.5 1.5.67 1.5 1.5-.67 1.5-1.5 1.5zM5 11l1.5-4.5h11L19 11z" />
    </svg>
  );
}

/** Split an EFA route label into a compact chip code + descriptive name. EFA hands us values like
 *  "T9 Northern Line", "M1 Metro North West & Bankstown Line", "L3 Kingsford Line", a bare bus
 *  number ("392"), or a name with no code ("Central Coast & Newcastle Line"). We pull a leading
 *  short code (letter(s)+digits, or pure digits) for the colour chip so a long line name never
 *  overflows it; the remainder (or the mode name) becomes wrapping text beside the chip. */
function splitRouteLabel(
  routeShortName: string | undefined,
  mode: string,
): { code: string; name: string } {
  const fallbackCode = legMark(mode) ?? "•";
  if (!routeShortName) return { code: fallbackCode, name: legModeName(mode) };
  const trimmed = routeShortName.trim();
  const m = trimmed.match(/^([A-Za-z]{1,2}\d{1,3}[A-Za-z]?|\d{1,4})\b\s*(.*)$/);
  if (m) {
    const rest = m[2].trim();
    return { code: m[1], name: rest || legModeName(mode) };
  }
  return { code: fallbackCode, name: trimmed };
}

/**
 * Google-Maps-style timed itinerary for one passenger's home → pickup journey, shown in the hover
 * InfoWindow. When the backend supplied per-leg detail (`pathLegs`), it renders a vertical timeline:
 * each stop is a node with its scheduled clock time, and each connecting segment shows its mode
 * (walk glyph or a TfNSW line chip), the line label, and the leg's minutes. Falls back to a compact
 * walk/PT minute summary when only aggregate minutes are available (stub data, or candidate nodes
 * generated before per-leg detail existed).
 */
function JourneyDetail({ leg }: { leg: PassengerLeg }): React.JSX.Element {
  const total = leg.walkMins + leg.ptMins;
  const legs = leg.pathLegs;

  if (!legs || legs.length === 0) {
    return (
      <div className="space-y-0.5 pt-1 text-slate-600">
        <div className="flex items-center gap-1">
          <WalkIcon className="shrink-0 text-slate-500" />
          <span>Walk to pickup:</span>
          <span className="font-medium text-slate-800">
            {leg.walkMins > 0 ? `${leg.walkMins} min` : "—"}
          </span>
        </div>
        {leg.ptMins > 0 ? (
          <div className="flex items-center gap-1">
            {transitStyle(leg.mode) ? (
              <TransitBadge modality={leg.mode} size={14} />
            ) : (
              <span>🚆</span>
            )}
            <span>{transitStyle(leg.mode)?.name ?? "Public transport"}:</span>
            <span className="font-medium text-slate-800">{leg.ptMins} min</span>
          </div>
        ) : null}
      </div>
    );
  }

  const depart = formatClock(legs[0].departureTime);
  const arrive = formatClock(legs[legs.length - 1].arrivalTime);

  // Node i is the stop leg i departs from; the trailing node (i === legs.length) is the pickup.
  // A node's clock time is the previous leg's arrival (or, for the first node, leg 0's departure).
  const nodeTime = (i: number): string | null => {
    if (i === 0) return formatClock(legs[0].departureTime);
    if (i >= legs.length) return formatClock(legs[legs.length - 1].arrivalTime);
    return formatClock(legs[i - 1].arrivalTime) ?? formatClock(legs[i].departureTime);
  };
  const nodeName = (i: number): string => {
    if (i === 0) return legs[0].fromName ?? "Home";
    if (i >= legs.length) return legs[legs.length - 1].toName ?? "Pickup point";
    return legs[i].fromName ?? legs[i - 1].toName ?? "Stop";
  };

  return (
    <div className="pt-1" style={{ minWidth: 215, maxWidth: 270 }}>
      <div className="mb-1.5 flex items-center justify-between border-b border-slate-200 pb-1 text-[11px]">
        <span className="font-semibold text-slate-800">
          {depart && arrive ? `${depart} – ${arrive}` : "Journey to pickup"}
        </span>
        {total > 0 ? <span className="text-slate-500">{total} min</span> : null}
      </div>

      {legs.map((seg, i) => {
        const style = legStyle(seg.mode);
        const { code, name } = splitRouteLabel(seg.routeShortName, seg.mode);
        const dur = seg.durationMins ? ` · ${seg.durationMins} min` : "";
        return (
          <div key={i} className="flex gap-2">
            <div className="w-9 shrink-0 pt-px text-right text-[10px] tabular-nums leading-tight text-slate-500">
              {nodeTime(i) ?? ""}
            </div>
            <div className="flex flex-col items-center">
              <span
                className="mt-px h-2 w-2 shrink-0 rounded-full ring-2 ring-white"
                style={{ backgroundColor: style.color }}
              />
              <span
                className="w-0.5 grow rounded"
                style={{ backgroundColor: style.color, minHeight: 18, opacity: 0.45 }}
              />
            </div>
            <div className="min-w-0 flex-1 pb-2 text-[11px] leading-tight">
              <div className="font-medium text-slate-800">{nodeName(i)}</div>
              <div className="mt-0.5 flex items-start gap-1 text-slate-600">
                {style.isWalk ? (
                  <WalkIcon className="mt-px shrink-0 text-slate-500" />
                ) : (
                  <span
                    className="mt-px inline-flex shrink-0 items-center justify-center rounded-[3px] px-1 py-px text-[9px] font-bold leading-none text-white"
                    style={{ backgroundColor: style.color }}
                  >
                    {code}
                  </span>
                )}
                <span className="min-w-0 break-words">
                  {style.isWalk ? `Walk${dur}` : `${name}${dur}`}
                </span>
              </div>
            </div>
          </div>
        );
      })}

      {/* Final pickup node — green to match the map's pickup pins. */}
      <div className="flex gap-2">
        <div className="w-9 shrink-0 pt-px text-right text-[10px] tabular-nums leading-tight text-slate-500">
          {nodeTime(legs.length) ?? ""}
        </div>
        <div className="flex flex-col items-center">
          <span className="mt-px h-2.5 w-2.5 shrink-0 rounded-full bg-[#34A853] ring-2 ring-white" />
        </div>
        <div className="flex-1 text-[11px] leading-tight">
          <div className="font-medium text-slate-800">{nodeName(legs.length)}</div>
          <div className="mt-0.5 text-slate-500">{leg.driverName} picks up here</div>
        </div>
      </div>
    </div>
  );
}

/**
 * Driving itinerary for a hovered driver — the same vertical timeline as the passenger view, but
 * tracing the car: Home → each pickup stop (with who's collected there and the scheduled arrival)
 * → destination. Drive segments are drawn in the driver's route colour.
 */
function DriverDetail({
  route,
  destinationAddress,
  arriveBy,
  participantsById,
}: {
  route: SolutionRoute;
  destinationAddress: string;
  arriveBy?: string;
  participantsById: Record<string, Participant>;
}): React.JSX.Element {
  const colour = route.colour;
  // Prefer the backend's estimated destination arrival — the timeline is anchored so the driver
  // lands a few minutes before the target rather than idling. Fall back to the trip's target
  // arriveBy only when it's missing, since arriveBy is the goal, not the ETA.
  const arrive = formatClock(route.destinationArrival) ?? formatClock(arriveBy);
  // When the driver leaves home — surfaced so the timeline reads depart → pickups → arrive.
  const depart = formatClock(route.departure);

  const stops = route.stops.map((s) => ({
    time: formatClock(s.arriveAt),
    kind: s.nodeKind,
    names: s.pickupLegs
      .map((l) => participantsById[l.participantId]?.displayName)
      .filter((n): n is string => Boolean(n)),
  }));

  const driveLeg = (
    <div className="mt-0.5 flex items-center gap-1 text-slate-600">
      <span className="shrink-0" style={{ color: colour }}>
        <CarIcon />
      </span>
      <span>Drive</span>
    </div>
  );
  const dot = (
    <span
      className="mt-px h-2 w-2 shrink-0 rounded-full ring-2 ring-white"
      style={{ backgroundColor: colour }}
    />
  );
  const rail = (
    <span
      className="w-0.5 grow rounded"
      style={{ backgroundColor: colour, minHeight: 18, opacity: 0.45 }}
    />
  );

  return (
    <div className="pt-1" style={{ minWidth: 215, maxWidth: 270 }}>
      <div className="mb-1.5 flex items-center justify-between border-b border-slate-200 pb-1 text-[11px]">
        <span className="font-semibold text-slate-800">
          {depart && arrive
            ? `Departs ${depart} · Arrives ${arrive}`
            : arrive
              ? `Arrives ${arrive}`
              : "Driving route"}
        </span>
        <span className="shrink-0 pl-2 text-slate-500">{Math.round(route.drivingMinutes)} min driving</span>
      </div>

      {/* Home */}
      <div className="flex gap-2">
        <div className="w-9 shrink-0 pt-px text-right text-[10px] tabular-nums leading-tight text-slate-500">
          {depart ?? ""}
        </div>
        <div className="flex flex-col items-center">
          {dot}
          {rail}
        </div>
        <div className="min-w-0 flex-1 pb-2 text-[11px] leading-tight">
          <div className="font-medium text-slate-800">Home</div>
          {driveLeg}
        </div>
      </div>

      {/* Pickup stops */}
      {stops.map((s, i) => (
        <div key={i} className="flex gap-2">
          <div className="w-9 shrink-0 pt-px text-right text-[10px] tabular-nums leading-tight text-slate-500">
            {s.time ?? ""}
          </div>
          <div className="flex flex-col items-center">
            {dot}
            {rail}
          </div>
          <div className="min-w-0 flex-1 pb-2 text-[11px] leading-tight">
            <div className="flex items-center gap-1 font-medium text-slate-800">
              {transitStyle(s.kind) ? <TransitBadge modality={s.kind} size={13} /> : null}
              <span className="min-w-0 break-words">
                {s.names.length > 0 ? `Pick up ${s.names.join(", ")}` : "Pickup"}
              </span>
            </div>
            {driveLeg}
          </div>
        </div>
      ))}

      {/* Destination */}
      <div className="flex gap-2">
        <div className="w-9 shrink-0 pt-px text-right text-[10px] tabular-nums leading-tight text-slate-500">
          {arrive ?? ""}
        </div>
        <div className="flex flex-col items-center">
          <span className="mt-px h-2.5 w-2.5 shrink-0 rounded-full bg-[#EA4335] ring-2 ring-white" />
        </div>
        <div className="min-w-0 flex-1 text-[11px] leading-tight">
          <div className="font-medium text-slate-800">Destination</div>
          <div className="mt-0.5 break-words text-slate-500">{destinationAddress}</div>
        </div>
      </div>
    </div>
  );
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
