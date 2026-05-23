"use client";

// Non-Mapbox map view used when NEXT_PUBLIC_MAPBOX_TOKEN is not configured.
// Renders the trip's origins, destination, candidate nodes, and (optionally)
// a locked solution's routes + stops as positioned points on an SVG. Keeps
// the planner/driver screenshots meaningful in offline/demo environments
// where no Mapbox basemap is available.

import { useMemo } from "react";
import type { CandidateNode, LatLng, Participant, Solution } from "@/lib/api/schema";

interface BBox {
  minLat: number;
  maxLat: number;
  minLng: number;
  maxLng: number;
}

interface Projected extends LatLng {
  x: number;
  y: number;
}

export interface MapFallbackProps {
  destination: { address: string; point: LatLng };
  participants?: Participant[];
  candidateNodes?: CandidateNode[];
  solution?: Solution;
  driverPosition?: LatLng;
  participantHome?: LatLng;
  highlightStopIndex?: number;
}

const WIDTH = 800;
const HEIGHT = 600;
const PADDING = 50;

function collect(p: MapFallbackProps): LatLng[] {
  const out: LatLng[] = [p.destination.point];
  for (const part of p.participants ?? []) out.push(part.origin);
  for (const cn of p.candidateNodes ?? []) out.push(cn.location);
  for (const route of p.solution?.routes ?? []) {
    for (const point of route.polyline) out.push(point);
    for (const stop of route.stops) out.push(stop.location);
  }
  if (p.driverPosition) out.push(p.driverPosition);
  if (p.participantHome) out.push(p.participantHome);
  return out;
}

function bbox(points: LatLng[]): BBox {
  const lats = points.map((p) => p.lat);
  const lngs = points.map((p) => p.lng);
  return {
    minLat: Math.min(...lats),
    maxLat: Math.max(...lats),
    minLng: Math.min(...lngs),
    maxLng: Math.max(...lngs),
  };
}

function makeProjector(b: BBox): (p: LatLng) => Projected {
  // Pad bounds slightly so points don't sit on the edge.
  const latSpan = (b.maxLat - b.minLat) * 1.15 || 0.02;
  const lngSpan = (b.maxLng - b.minLng) * 1.15 || 0.02;
  const cLat = (b.minLat + b.maxLat) / 2;
  const cLng = (b.minLng + b.maxLng) / 2;
  const innerW = WIDTH - 2 * PADDING;
  const innerH = HEIGHT - 2 * PADDING;
  return (p: LatLng) => {
    const x = ((p.lng - (cLng - lngSpan / 2)) / lngSpan) * innerW + PADDING;
    // SVG y grows downward; latitude grows northward — flip.
    const y = innerH - ((p.lat - (cLat - latSpan / 2)) / latSpan) * innerH + PADDING;
    return { ...p, x, y };
  };
}

export function MapFallback(props: MapFallbackProps): React.JSX.Element {
  const allPoints = collect(props);
  const projector = useMemo(
    () => makeProjector(bbox(allPoints.length ? allPoints : [props.destination.point])),
    // eslint-disable-next-line react-hooks/exhaustive-deps
    [
      props.destination.point.lat,
      props.destination.point.lng,
      props.participants?.length,
      props.candidateNodes?.length,
      props.solution?.id,
    ],
  );

  const dest = projector(props.destination.point);
  const origins = (props.participants ?? []).map((p) => ({
    ...p,
    projected: projector(p.origin),
  }));
  const nodes = (props.candidateNodes ?? []).map((c) => projector(c.location));
  const routes = (props.solution?.routes ?? []).map((r) => ({
    colour: r.colour,
    line: r.polyline.map(projector),
    stops: r.stops.map((s, idx) => ({
      idx,
      projected: projector(s.location),
      passengers: s.passengerIds.length,
    })),
  }));
  const driver = props.driverPosition ? projector(props.driverPosition) : null;
  const home = props.participantHome ? projector(props.participantHome) : null;

  return (
    <div className="bg-muted/30 relative h-full w-full overflow-hidden">
      <svg
        viewBox={`0 0 ${WIDTH} ${HEIGHT}`}
        preserveAspectRatio="xMidYMid meet"
        className="absolute inset-0 h-full w-full"
        role="img"
        aria-label="Trip map (basemap disabled)"
        data-testid="map-fallback"
      >
        <defs>
          <pattern id="grid" width="40" height="40" patternUnits="userSpaceOnUse">
            <path
              d="M 40 0 L 0 0 0 40"
              fill="none"
              stroke="#E2E8F0"
              strokeWidth="1"
            />
          </pattern>
        </defs>
        <rect width={WIDTH} height={HEIGHT} fill="url(#grid)" />

        {/* Candidate PT nodes — small grey */}
        {nodes.map((n, i) => (
          <circle
            key={`cn-${i}`}
            cx={n.x}
            cy={n.y}
            r={3.5}
            fill="#9CA3AF"
            stroke="#4B5563"
            strokeWidth={0.5}
            opacity={0.7}
          />
        ))}

        {/* Solution routes */}
        {routes.map((r, idx) => (
          <g key={`route-${idx}`}>
            <polyline
              points={r.line.map((p) => `${p.x},${p.y}`).join(" ")}
              fill="none"
              stroke={r.colour}
              strokeWidth={4}
              strokeOpacity={0.8}
              strokeLinecap="round"
              strokeLinejoin="round"
            />
            {r.stops.map((s) => (
              <g key={`stop-${idx}-${s.idx}`}>
                <circle
                  cx={s.projected.x}
                  cy={s.projected.y}
                  r={s.idx === props.highlightStopIndex ? 10 : 7}
                  fill={s.idx === props.highlightStopIndex ? "#F59E0B" : "#10B981"}
                  stroke="#065F46"
                  strokeWidth={2}
                />
                <text
                  x={s.projected.x}
                  y={s.projected.y + 3.5}
                  fontSize={9}
                  fontFamily="ui-sans-serif, system-ui"
                  fontWeight={600}
                  textAnchor="middle"
                  fill="#FFFFFF"
                >
                  {s.idx + 1}
                </text>
              </g>
            ))}
          </g>
        ))}

        {/* Participant origins */}
        {origins.map((o) => (
          <g key={`origin-${o.id}`}>
            <circle
              cx={o.projected.x}
              cy={o.projected.y}
              r={o.role === "driver" ? 8 : 5}
              fill="#DC2626"
              stroke="#7F1D1D"
              strokeWidth={1.5}
            />
            <text
              x={o.projected.x + 11}
              y={o.projected.y + 3}
              fontSize={11}
              fontFamily="ui-sans-serif, system-ui"
              fill="#111827"
            >
              {o.displayName}
            </text>
          </g>
        ))}

        {/* Live home (passenger live view) */}
        {home ? (
          <circle
            cx={home.x}
            cy={home.y}
            r={5}
            fill="#DC2626"
            stroke="#7F1D1D"
            strokeWidth={1.5}
          />
        ) : null}

        {/* Live driver position */}
        {driver ? (
          <g>
            <circle
              cx={driver.x}
              cy={driver.y}
              r={11}
              fill="#1D4ED8"
              stroke="#FFFFFF"
              strokeWidth={2.5}
            />
            <text
              x={driver.x}
              y={driver.y + 3.5}
              fontSize={10}
              textAnchor="middle"
              fill="#FFFFFF"
              fontWeight={700}
            >
              D
            </text>
          </g>
        ) : null}

        {/* Destination star */}
        <g transform={`translate(${dest.x}, ${dest.y})`}>
          <circle r={14} fill="#FBBF24" opacity={0.4} />
          <text
            fontSize={26}
            textAnchor="middle"
            dominantBaseline="central"
            fill="#111827"
            fontFamily="ui-sans-serif, system-ui"
          >
            ★
          </text>
        </g>
      </svg>
      <div className="bg-background/80 absolute bottom-2 left-2 rounded px-2 py-1 text-xs text-muted-foreground shadow-sm">
        <span className="font-medium">{props.destination.address}</span>
        <span className="ml-2">Basemap disabled (no Mapbox token)</span>
      </div>
    </div>
  );
}
