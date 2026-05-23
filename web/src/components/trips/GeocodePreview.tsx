"use client";

import { useEffect, useRef, useState } from "react";
import { MapPin, Loader2 } from "lucide-react";
import type { LatLng } from "@/lib/api/schema";

interface GeocodePreviewProps {
  address: string;
}

interface MapboxFeature {
  center: [number, number];
  place_name: string;
}

interface MapboxResponse {
  features?: MapboxFeature[];
}

type GeocodeOutcome =
  | { kind: "ok"; query: string; result: { label: string; point: LatLng } }
  | { kind: "error"; query: string };

/**
 * Lightweight client-side geocode preview using Mapbox's public Geocoding API.
 * It's intentionally separate from the canonical geocode the backend will run
 * on /trips/{id}/participants — this is just a "did the user type a real
 * address" affordance.
 *
 * If no NEXT_PUBLIC_MAPBOX_TOKEN is configured we degrade silently.
 */
export function GeocodePreview({ address }: GeocodePreviewProps): React.JSX.Element | null {
  const trimmed = address.trim();
  const token = process.env.NEXT_PUBLIC_MAPBOX_TOKEN;
  const [outcome, setOutcome] = useState<GeocodeOutcome | null>(null);
  const lastQuery = useRef<string>("");

  useEffect(() => {
    if (!token || trimmed.length < 4) return;
    const controller = new AbortController();
    lastQuery.current = trimmed;
    const url =
      `https://api.mapbox.com/geocoding/v5/mapbox.places/${encodeURIComponent(trimmed)}.json` +
      `?access_token=${token}&country=au&limit=1&bbox=150.5,-34.4,151.5,-33.4`;
    const timer = setTimeout(() => {
      fetch(url, { signal: controller.signal })
        .then((res) => (res.ok ? (res.json() as Promise<MapboxResponse>) : null))
        .then((json) => {
          if (lastQuery.current !== trimmed) return;
          const feature = json?.features?.[0];
          if (!feature) {
            setOutcome({ kind: "error", query: trimmed });
            return;
          }
          setOutcome({
            kind: "ok",
            query: trimmed,
            result: {
              label: feature.place_name,
              point: { lng: feature.center[0], lat: feature.center[1] },
            },
          });
        })
        .catch((err) => {
          if (err instanceof DOMException && err.name === "AbortError") return;
          if (lastQuery.current === trimmed) setOutcome({ kind: "error", query: trimmed });
        });
    }, 400);
    return () => {
      controller.abort();
      clearTimeout(timer);
    };
  }, [trimmed, token]);

  // Derived render state: loading is "the user typed something queryable but
  // no outcome for that exact query has come back yet". This avoids a
  // setState-inside-effect that the React Compiler rule (correctly) flags.
  const queryable = Boolean(token) && trimmed.length >= 4;
  const matchedOutcome = outcome && outcome.query === trimmed ? outcome : null;
  const status: "idle" | "loading" | "ok" | "error" = !queryable
    ? "idle"
    : matchedOutcome
      ? matchedOutcome.kind
      : "loading";
  const result = matchedOutcome?.kind === "ok" ? matchedOutcome.result : null;

  if (!token) {
    return (
      <p className="text-muted-foreground flex items-center gap-2 text-xs">
        <MapPin className="h-3.5 w-3.5" />
        Set NEXT_PUBLIC_MAPBOX_TOKEN to enable address preview.
      </p>
    );
  }

  if (status === "loading") {
    return (
      <p className="text-muted-foreground flex items-center gap-2 text-xs">
        <Loader2 className="h-3.5 w-3.5 animate-spin" /> Resolving address…
      </p>
    );
  }

  if (result) {
    return (
      <p className="text-muted-foreground flex items-center gap-2 text-xs">
        <MapPin className="text-foreground h-3.5 w-3.5" />
        <span>
          {result.label} ({result.point.lat.toFixed(4)}, {result.point.lng.toFixed(4)})
        </span>
      </p>
    );
  }

  if (status === "error") {
    return (
      <p className="text-muted-foreground text-xs">
        No match yet — keep typing or include a suburb / postcode.
      </p>
    );
  }

  return null;
}

