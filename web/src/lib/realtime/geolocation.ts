// Throttled geolocation watcher for the driver view. Browsers fire
// `geolocation.watchPosition` very frequently (often >1 Hz); we throttle to
// one update per `intervalMs` (default 5 s) before forwarding to the consumer
// so SignalR isn't flooded.
//
// Also exposes a simulated-position mode for development — the dev "Use
// simulated position" button cycles through a small Sydney polyline so the
// page works without a real fix.

"use client";

import { useEffect, useRef, useState } from "react";

export interface GeoFix {
  lat: number;
  lng: number;
  /** Accuracy in metres, when the source provides it. */
  accuracyM?: number;
  /** ISO-8601 timestamp at fix time. */
  ts: string;
  source: "geolocation" | "simulated";
}

export interface UseDriverPositionOptions {
  enabled?: boolean;
  /** Min interval between forwarded updates (ms). Defaults to 5000. */
  throttleMs?: number;
  /** Pass `true` to use the dev simulation instead of real geolocation. */
  simulated?: boolean;
}

export type PermissionState = "unknown" | "prompt" | "granted" | "denied" | "unsupported";

export interface DriverPositionState {
  fix: GeoFix | null;
  permission: PermissionState;
  error: string | null;
}

// Coarse polyline through inner Sydney — Glebe → Camperdown → Newtown → Marrickville.
const SIM_PATH: Array<[number, number]> = [
  [-33.879, 151.187],
  [-33.886, 151.183],
  [-33.892, 151.181],
  [-33.898, 151.179],
  [-33.904, 151.182],
  [-33.911, 151.185],
  [-33.917, 151.189],
];

export function useDriverPosition(options: UseDriverPositionOptions = {}): DriverPositionState {
  const { enabled = true, throttleMs = 5000, simulated = false } = options;
  const [fix, setFix] = useState<GeoFix | null>(null);
  const [permission, setPermission] = useState<PermissionState>("unknown");
  const [error, setError] = useState<string | null>(null);
  const lastEmitRef = useRef<number>(0);

  // Real geolocation watcher.
  useEffect(() => {
    if (!enabled || simulated) return;
    if (typeof navigator === "undefined" || !navigator.geolocation) {
      // Defer the setState so the rule (rightly) doesn't see it as a sync
      // in-body call. Microtask is enough; we're only nudging React to skip
      // its in-effect-render heuristic.
      queueMicrotask(() => {
        setPermission("unsupported");
        setError("Geolocation is not available in this browser.");
      });
      return;
    }

    if (navigator.permissions?.query) {
      navigator.permissions
        .query({ name: "geolocation" as PermissionName })
        .then((res) => {
          setPermission(res.state as PermissionState);
          res.onchange = () => setPermission(res.state as PermissionState);
        })
        .catch(() => {
          /* not all browsers support permissions API for geolocation */
        });
    }

    const watchId = navigator.geolocation.watchPosition(
      (pos) => {
        const now = Date.now();
        if (now - lastEmitRef.current < throttleMs) return;
        lastEmitRef.current = now;
        setError(null);
        setPermission("granted");
        setFix({
          lat: pos.coords.latitude,
          lng: pos.coords.longitude,
          accuracyM: pos.coords.accuracy,
          ts: new Date(pos.timestamp).toISOString(),
          source: "geolocation",
        });
      },
      (err) => {
        if (err.code === err.PERMISSION_DENIED) {
          setPermission("denied");
          setError(
            "Location permission denied. Re-grant access in your browser settings to broadcast your position.",
          );
        } else {
          setError(err.message || "Could not read your position.");
        }
      },
      { enableHighAccuracy: true, maximumAge: 1000, timeout: 15_000 },
    );

    return () => navigator.geolocation.clearWatch(watchId);
  }, [enabled, simulated, throttleMs]);

  // Simulated mode — cycle through SIM_PATH on a timer.
  useEffect(() => {
    if (!simulated || !enabled) return;
    let idx = 0;
    queueMicrotask(() => setPermission("granted"));
    const tick = (): void => {
      const [lat, lng] = SIM_PATH[idx % SIM_PATH.length];
      setFix({
        lat,
        lng,
        ts: new Date().toISOString(),
        source: "simulated",
      });
      idx += 1;
    };
    tick();
    const id = setInterval(tick, throttleMs);
    return () => clearInterval(id);
  }, [simulated, enabled, throttleMs]);

  return { fix, permission, error };
}
