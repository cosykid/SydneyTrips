"use client";

import { useEffect } from "react";
import { usePathname } from "next/navigation";
import { APIProvider, AdvancedMarker, Map, Pin, useMap } from "@vis.gl/react-google-maps";
import { useMapBackdrop, type MapFocus, type MapPin } from "@/lib/mapStore";
import { SYDNEY_CBD } from "@/lib/store";

const MAP_ID = process.env.NEXT_PUBLIC_GOOGLE_MAPS_MAP_ID ?? "DEMO_MAP_ID";

/** The planner / driver / passenger routes mount their own full map, so the
 *  shared backdrop steps aside there to avoid stacking two live instances. */
function ownsItsOwnMap(pathname: string): boolean {
  return /\/(plan|driver|passenger)\/?$/.test(pathname);
}

/**
 * The always-on Sydney map that lives in the app shell, beneath every
 * floating overlay page. Renders the real Google map when a key is present;
 * otherwise a deterministic Sydney-flavoured backdrop (CI, Playwright, no key).
 */
export function MapBackdrop(): React.JSX.Element | null {
  const pathname = usePathname() ?? "";
  const apiKey = process.env.NEXT_PUBLIC_GOOGLE_MAPS_KEY;

  if (ownsItsOwnMap(pathname)) return null;
  if (!apiKey) return <FallbackBackdrop />;

  return (
    // Match PlanMap/LiveMap's libraries so the Maps JS API loads with identical
    // params across routes (otherwise the API warns on the second loader).
    <APIProvider apiKey={apiKey} libraries={["routes"]}>
      <BackdropMap />
    </APIProvider>
  );
}

function BackdropMap(): React.JSX.Element {
  const focus = useMapBackdrop((s) => s.focus);
  return (
    <Map
      mapId={MAP_ID}
      defaultCenter={{ lat: SYDNEY_CBD.latitude, lng: SYDNEY_CBD.longitude }}
      defaultZoom={SYDNEY_CBD.zoom}
      gestureHandling="greedy"
      disableDefaultUI
      clickableIcons={false}
      style={{ width: "100%", height: "100%" }}
    >
      <CameraController focus={focus} />
      {focus.pins.map((pin) => (
        <BackdropMarker key={pin.id} pin={pin} />
      ))}
    </Map>
  );
}

/** Pans/zooms the (otherwise uncontrolled) map when a page publishes a focus
 *  point, leaving the user free to drag afterwards. Imperative on purpose so
 *  the camera isn't a controlled prop fighting user gestures. */
function CameraController({ focus }: { focus: MapFocus }): null {
  const map = useMap();
  useEffect(() => {
    if (!map || !focus.center) return;
    map.panTo({ lat: focus.center.lat, lng: focus.center.lng });
    if (focus.zoom != null) map.setZoom(focus.zoom);
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [map, focus.center?.lat, focus.center?.lng, focus.zoom]);
  return null;
}

function BackdropMarker({ pin }: { pin: MapPin }): React.JSX.Element {
  if (pin.kind === "destination") {
    return (
      <AdvancedMarker position={pin.position} title={pin.label}>
        <Pin background="#EA4335" borderColor="#a52714" glyphColor="#7a1d0e" scale={1.1} />
      </AdvancedMarker>
    );
  }
  const colour = pin.kind === "driver" ? "#0e7c86" : pin.kind === "home" ? "#9334e6" : "#0e7c86";
  return (
    <AdvancedMarker position={pin.position} title={pin.label}>
      <div className="flex flex-col items-center">
        {pin.label ? (
          <span className="mb-1 max-w-[160px] truncate rounded-full bg-white px-2 py-0.5 text-[11px] font-medium text-[#3c4043] shadow-google">
            {pin.label}
          </span>
        ) : null}
        <span
          className="h-3.5 w-3.5 rounded-full ring-2 ring-white"
          style={{ backgroundColor: colour, boxShadow: "0 1px 3px rgba(60,64,67,.4)" }}
        />
      </div>
    </AdvancedMarker>
  );
}

/* --------------------------------------------------------------------------
 * No-key fallback: a calm, abstract Sydney map. Pure SVG + a few positioned
 * pins, projected onto a fixed Sydney window so trip markers land roughly
 * where they belong. Only used when NEXT_PUBLIC_GOOGLE_MAPS_KEY is unset.
 * ------------------------------------------------------------------------ */

// Window: a generous box around metropolitan Sydney.
const WIN = { latN: -33.6, latS: -34.05, lngW: 150.95, lngE: 151.4 };

function project(lat: number, lng: number): { left: string; top: string } {
  const x = ((lng - WIN.lngW) / (WIN.lngE - WIN.lngW)) * 100;
  const y = ((WIN.latN - lat) / (WIN.latN - WIN.latS)) * 100;
  return { left: `${Math.max(0, Math.min(100, x))}%`, top: `${Math.max(0, Math.min(100, y))}%` };
}

function FallbackBackdrop(): React.JSX.Element {
  const focus = useMapBackdrop((s) => s.focus);
  return (
    <div className="relative h-full w-full overflow-hidden bg-[#e9edf0]" data-testid="map-backdrop-fallback">
      <svg
        viewBox="0 0 1440 1024"
        preserveAspectRatio="xMidYMid slice"
        className="absolute inset-0 h-full w-full"
        aria-hidden
      >
        <defs>
          <pattern id="bd-roads" width="96" height="96" patternUnits="userSpaceOnUse">
            <path d="M96 0 L0 0 0 96" fill="none" stroke="#dfe3e6" strokeWidth="2" />
          </pattern>
        </defs>
        <rect width="1440" height="1024" fill="#eef1f3" />
        <rect width="1440" height="1024" fill="url(#bd-roads)" />
        {/* Green spaces — Centennial Park, Royal Botanic, North Shore bush. */}
        <path d="M900 470 q60 -40 130 -10 q40 60 -10 120 q-90 40 -150 -20 q-20 -60 30 -90 Z" fill="#cfe6cc" />
        <path d="M250 250 q80 -30 140 20 q20 70 -40 110 q-90 20 -130 -50 q-10 -50 30 -80 Z" fill="#d4e8d0" />
        {/* The harbour — a ragged blue waterway slicing in from the east. */}
        <path
          d="M1440 300 C1180 320 1120 230 980 270 C880 300 860 360 760 350 C690 343 660 300 600 318 C560 330 560 380 600 400 C700 430 760 380 880 410 C1010 442 1080 380 1440 430 Z"
          fill="#a8d3ec"
        />
        {/* Botany Bay, lower right. */}
        <path d="M980 1024 C980 900 1080 860 1200 880 C1340 904 1380 980 1440 1010 L1440 1024 Z" fill="#a8d3ec" />
        {/* Open ocean band along the right edge. */}
        <path d="M1330 0 C1360 200 1320 360 1360 540 C1390 700 1340 880 1380 1024 L1440 1024 L1440 0 Z" fill="#9ecae6" />
        {/* A couple of arterial roads for a sense of place. */}
        <path d="M120 760 C420 700 560 560 760 470 C940 388 1100 360 1330 360" fill="none" stroke="#ffffff" strokeWidth="6" opacity="0.8" />
        <path d="M300 1010 C360 760 520 620 640 380 C700 260 720 140 740 0" fill="none" stroke="#ffffff" strokeWidth="6" opacity="0.8" />
      </svg>

      {focus.pins.map((pin) => {
        const pos = project(pin.position.lat, pin.position.lng);
        const isDest = pin.kind === "destination";
        return (
          <div
            key={pin.id}
            className="absolute flex -translate-x-1/2 -translate-y-full flex-col items-center"
            style={{ left: pos.left, top: pos.top }}
          >
            {pin.label ? (
              <span className="mb-1 max-w-[160px] truncate rounded-full bg-white px-2 py-0.5 text-[11px] font-medium text-[#3c4043] shadow-google">
                {pin.label}
              </span>
            ) : null}
            <span
              className="rounded-full ring-2 ring-white"
              style={{
                width: isDest ? 16 : 13,
                height: isDest ? 16 : 13,
                backgroundColor: isDest ? "#EA4335" : pin.kind === "home" ? "#9334e6" : "#0e7c86",
                boxShadow: "0 1px 3px rgba(60,64,67,.4)",
              }}
            />
          </div>
        );
      })}
    </div>
  );
}
