"use client";

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
  const latSpan = (b.maxLat - b.minLat) * 1.15 || 0.02;
  const lngSpan = (b.maxLng - b.minLng) * 1.15 || 0.02;
  const cLat = (b.minLat + b.maxLat) / 2;
  const cLng = (b.minLng + b.maxLng) / 2;
  const innerW = WIDTH - 2 * PADDING;
  const innerH = HEIGHT - 2 * PADDING;
  return (p: LatLng) => {
    const x = ((p.lng - (cLng - lngSpan / 2)) / lngSpan) * innerW + PADDING;
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
    <div className="relative h-full w-full overflow-hidden bg-[#F8FAFC]">
      <svg
        viewBox={`0 0 ${WIDTH} ${HEIGHT}`}
        preserveAspectRatio="xMidYMid meet"
        className="absolute inset-0 h-full w-full"
        role="img"
        aria-label="Trip map"
        data-testid="map-fallback"
      >
        <defs>
          <pattern id="grid" width="40" height="40" patternUnits="userSpaceOnUse">
            <path
              d="M 40 0 L 0 0 0 40"
              fill="none"
              stroke="#E8EAED"
              strokeWidth="1"
            />
          </pattern>
        </defs>
        <rect width={WIDTH} height={HEIGHT} fill="url(#grid)" />

        {nodes.map((n, i) => (
          <circle
            key={`cn-${i}`}
            cx={n.x}
            cy={n.y}
            r={3.5}
            fill="#94A3B8"
            stroke="#475569"
            strokeWidth={0.5}
            opacity={0.55}
          />
        ))}

        {routes.map((r, idx) => (
          <g key={`route-${idx}`}>
            <polyline
              points={r.line.map((p) => `${p.x},${p.y}`).join(" ")}
              fill="none"
              stroke={r.colour}
              strokeWidth={5}
              strokeOpacity={0.9}
              strokeLinecap="round"
              strokeLinejoin="round"
            />
            {r.stops.map((s) => (
              <g key={`stop-${idx}-${s.idx}`}>
                <circle
                  cx={s.projected.x}
                  cy={s.projected.y}
                  r={s.idx === props.highlightStopIndex ? 10 : 7}
                  fill={s.idx === props.highlightStopIndex ? "#FBBC04" : "#34A853"}
                  stroke="#FFFFFF"
                  strokeWidth={2.5}
                />
                <text
                  x={s.projected.x}
                  y={s.projected.y + 3.5}
                  fontSize={9}
                  fontFamily="ui-sans-serif, system-ui"
                  fontWeight={600}
                  textAnchor="middle"
                  fill={s.idx === props.highlightStopIndex ? "#111827" : "#FFFFFF"}
                >
                  {s.idx + 1}
                </text>
              </g>
            ))}
          </g>
        ))}

        {origins.map((o) => (
          <g key={`origin-${o.id}`}>
            <circle
              cx={o.projected.x}
              cy={o.projected.y}
              r={o.role === "driver" ? 8 : 5}
              fill="#0E7C86"
              stroke="#FFFFFF"
              strokeWidth={2}
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

        {home ? (
          <circle
            cx={home.x}
            cy={home.y}
            r={6}
            fill="#EA4335"
            stroke="#FFFFFF"
            strokeWidth={2}
          />
        ) : null}

        {driver ? (
          <g>
            <circle
              cx={driver.x}
              cy={driver.y}
              r={12}
              fill="#0E7C86"
              stroke="#FFFFFF"
              strokeWidth={3}
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

        <g transform={`translate(${dest.x}, ${dest.y})`}>
          <circle r={14} fill="#FBBC04" opacity={0.4} />
          <text
            fontSize={28}
            textAnchor="middle"
            dominantBaseline="central"
            fill="#111827"
            fontFamily="ui-sans-serif, system-ui"
          >
            ★
          </text>
        </g>
      </svg>
      <div className="bg-card/95 ring-foreground/5 absolute bottom-3 right-3 flex items-center gap-2 rounded-full px-3 py-1.5 text-xs shadow-md ring-1 backdrop-blur-sm">
        <span className="text-foreground font-medium">{props.destination.address}</span>
        <span className="bg-muted text-muted-foreground rounded-full px-1.5 py-0.5 text-[10px] font-medium uppercase tracking-wider">
          Preview
        </span>
      </div>
    </div>
  );
}
