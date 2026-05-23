"use client";

// Driver live view — left side full-height map, right side manifest panel
// (pickup stops, ETAs, "Arrived" buttons, deep-link map apps, geolocation
// permission UI).

import Link from "next/link";
import dynamic from "next/dynamic";
import { useEffect, useMemo, useState } from "react";
import { ArrowLeft, ExternalLink, Loader2, MapPin, Navigation } from "lucide-react";
import { toast } from "sonner";
import { format } from "date-fns";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Separator } from "@/components/ui/separator";
import { ConnectionBadge } from "./ConnectionBadge";
import type { LiveMapProps } from "./LiveMap";
import { fitBounds } from "./LiveMap";
import { useLockedSolution, useTrip } from "@/lib/api/hooks";
import { useDriverPosition } from "@/lib/realtime/geolocation";
import { useTripHub } from "@/lib/realtime/hub";
import { SYDNEY_CBD, type MapViewState } from "@/lib/store";
import type {
  EtaUpdatedPayload,
  PassengerAtStopPayload,
  RouteRecomputedPayload,
} from "@/lib/realtime/hub";
import type { LatLng, Solution, SolutionRoute } from "@/lib/api/schema";

const LiveMap = dynamic<LiveMapProps>(() => import("./LiveMap").then((m) => m.LiveMap), {
  ssr: false,
  loading: () => (
    <div className="bg-muted/40 flex h-full w-full items-center justify-center">
      <Loader2 className="text-muted-foreground h-6 w-6 animate-spin" />
    </div>
  ),
});

export interface DriverViewProps {
  tripId: string;
  /** The participant id of the signed-in user, used to gate access. */
  currentUserParticipantId?: string;
}

interface StopRowProps {
  index: number;
  stop: SolutionRoute["stops"][number];
  passengerNames: Map<string, string>;
  eta: Date;
  arrived: boolean;
  onArrive: () => void;
  busy: boolean;
}

function StopRow({
  index,
  stop,
  passengerNames,
  eta,
  arrived,
  onArrive,
  busy,
}: StopRowProps): React.JSX.Element {
  const mapsHref = `https://maps.google.com/?daddr=${stop.location.lat},${stop.location.lng}`;
  const appleHref = `https://maps.apple.com/?daddr=${stop.location.lat},${stop.location.lng}`;
  return (
    <li
      className="flex flex-col gap-2 rounded-md border p-3"
      aria-current={arrived ? "false" : undefined}
    >
      <div className="flex items-center justify-between">
        <div className="flex items-center gap-2 text-sm font-medium">
          <span className="bg-muted text-foreground/80 flex h-6 w-6 items-center justify-center rounded-full text-xs">
            {index + 1}
          </span>
          <span>{format(eta, "HH:mm")}</span>
          <Badge variant={arrived ? "default" : "secondary"}>
            {arrived ? "Arrived" : `${stop.passengerIds.length} pickup${stop.passengerIds.length === 1 ? "" : "s"}`}
          </Badge>
        </div>
        <Button
          type="button"
          size="sm"
          variant={arrived ? "outline" : "default"}
          onClick={onArrive}
          disabled={busy || arrived}
        >
          {arrived ? "Confirmed" : "Arrived"}
        </Button>
      </div>
      <ul className="text-muted-foreground space-y-0.5 text-xs">
        {stop.passengerIds.map((pid) => (
          <li key={pid} className="flex items-center gap-1.5">
            <MapPin className="h-3 w-3" />
            <span className="text-foreground">{passengerNames.get(pid) ?? "Unknown rider"}</span>
            <span>· walk {stop.walkMetres.toFixed(0)} m</span>
          </li>
        ))}
      </ul>
      <div className="flex flex-wrap gap-1.5 text-xs">
        <a
          className="text-primary hover:underline"
          href={mapsHref}
          target="_blank"
          rel="noreferrer"
        >
          <ExternalLink className="mr-1 inline h-3 w-3" />
          Google Maps
        </a>
        <span className="text-muted-foreground">·</span>
        <a
          className="text-primary hover:underline"
          href={appleHref}
          target="_blank"
          rel="noreferrer"
        >
          <Navigation className="mr-1 inline h-3 w-3" />
          Apple Maps
        </a>
      </div>
    </li>
  );
}

function findDriverRoute(
  solution: Solution | null | undefined,
  participantId: string | undefined,
): SolutionRoute | undefined {
  if (!solution || !participantId) return undefined;
  return solution.routes.find((r) => r.driverParticipantId === participantId);
}

export function DriverView({
  tripId,
  currentUserParticipantId,
}: DriverViewProps): React.JSX.Element {
  const trip = useTrip(tripId);
  const locked = useLockedSolution(tripId);
  const [busy, setBusy] = useState(false);
  const [arrivedStops, setArrivedStops] = useState<Record<string, boolean>>({});
  const [useSimulated, setUseSimulated] = useState(false);

  const driverRoute = useMemo(
    () => findDriverRoute(locked.data, currentUserParticipantId),
    [locked.data, currentUserParticipantId],
  );

  const passengerNames = useMemo<Map<string, string>>(() => {
    const map = new Map<string, string>();
    for (const p of trip.data?.participants ?? []) {
      map.set(p.id, p.displayName);
    }
    return map;
  }, [trip.data]);

  const allPoints = useMemo<LatLng[]>(() => {
    if (!driverRoute) return [];
    return [
      ...driverRoute.polyline,
      ...driverRoute.stops.map((s) => s.location),
    ];
  }, [driverRoute]);

  // Compute the initial view from the route the first time it loads; once the
  // user pans/zooms we keep their view (stored in `userView`). This avoids
  // calling setState from inside an effect, which React 19 flags.
  const fittedView = useMemo<MapViewState>(
    () => (allPoints.length ? fitBounds(allPoints, SYDNEY_CBD) : { ...SYDNEY_CBD }),
    [allPoints],
  );
  const [userView, setUserView] = useState<MapViewState | null>(null);
  const viewState = userView ?? fittedView;
  const setViewState = (v: MapViewState): void => setUserView(v);

  // Geolocation + hub wiring.
  const hub = useTripHub(tripId);
  const { fix, permission, error: geoError } = useDriverPosition({
    enabled: Boolean(driverRoute) && hub.status === "connected",
    simulated: useSimulated,
  });

  // Broadcast every fresh fix.
  useEffect(() => {
    if (!fix) return;
    hub.publishDriverPosition(fix.lat, fix.lng).catch(() => {
      /* swallowed inside hub */
    });
  }, [fix, hub]);

  // Toasts on hub events of interest.
  useEffect(() => {
    const off1 = hub.onEtaUpdated((p: EtaUpdatedPayload) => {
      const name = passengerNames.get(p.passengerId) ?? "A passenger";
      const newEta = new Date(p.newEta);
      toast.info(`${name} ETA updated`, {
        description: `Now ${format(newEta, "HH:mm")}`,
      });
    });
    const off2 = hub.onPassengerAtStop((p: PassengerAtStopPayload) => {
      const name = passengerNames.get(p.passengerId) ?? "A passenger";
      toast.success(`${name} is at the stop`);
    });
    const off3 = hub.onRouteRecomputed((p: RouteRecomputedPayload) => {
      toast.warning("Route recomputed", { description: `New solution ${p.solutionId.slice(0, 8)}` });
    });
    return () => {
      off1();
      off2();
      off3();
    };
  }, [hub, passengerNames]);

  async function onArrive(stopKey: string, passengerIds: string[]): Promise<void> {
    setBusy(true);
    try {
      // The server hub method as specced takes (tripId, stopId) per
      // passenger event. We fire one check-in per passenger at this stop so
      // the manifest stays in sync regardless of server-side fan-out.
      for (const pid of passengerIds) {
        await hub.passengerCheckIn(stopKey);
        // eslint-disable-next-line @typescript-eslint/no-unused-vars
        const _ = pid; // keep loop semantics — server uses driverId from auth
      }
      setArrivedStops((s) => ({ ...s, [stopKey]: true }));
      toast.success("Marked arrived");
    } catch (err) {
      toast.error("Could not mark arrived", {
        description: err instanceof Error ? err.message : undefined,
      });
    } finally {
      setBusy(false);
    }
  }

  if (trip.isLoading || locked.isLoading) {
    return (
      <div className="flex h-full items-center justify-center">
        <Loader2 className="text-muted-foreground h-6 w-6 animate-spin" />
      </div>
    );
  }

  if (!trip.data) {
    return (
      <div className="p-8 text-sm">
        Trip not found.
        <Link href="/trips" className="text-primary ml-2 underline">
          Back to trips
        </Link>
      </div>
    );
  }

  if (!locked.data) {
    return (
      <EmptyState
        title="No solution locked"
        body="Lock a solution from the planner before opening the driver view."
        tripId={tripId}
      />
    );
  }

  if (!driverRoute) {
    return (
      <EmptyState
        title="You are not the driver for this trip"
        body="This page is only available to the driver assigned in the locked solution."
        tripId={tripId}
      />
    );
  }

  return (
    <div className="flex h-full w-full">
      <div className="relative flex-1">
        <LiveMap
          destination={{ address: trip.data.destinationAddress, point: trip.data.destination }}
          route={driverRoute}
          stopsArrived={arrivedStops}
          driverPosition={fix ? { lat: fix.lat, lng: fix.lng } : undefined}
          viewState={viewState}
          onMove={setViewState}
        />
        <div className="bg-background/90 absolute left-4 top-4 flex items-center gap-2 rounded-md border px-3 py-1.5 text-xs shadow-sm">
          <Link href={`/trips/${tripId}`} className="flex items-center gap-1.5">
            <ArrowLeft className="h-3.5 w-3.5" /> {trip.data.name}
          </Link>
        </div>
        <div className="absolute right-4 top-4">
          <ConnectionBadge status={hub.status} error={hub.error} />
        </div>
      </div>
      <aside className="bg-card flex h-full w-96 flex-col overflow-y-auto border-l">
        <div className="space-y-4 p-5">
          <header>
            <h1 className="text-base font-semibold">Driver manifest</h1>
            <p className="text-muted-foreground text-xs">
              {driverRoute.stops.length} stop{driverRoute.stops.length === 1 ? "" : "s"} ·{" "}
              {driverRoute.drivingMinutes.toFixed(0)} min total
            </p>
          </header>

          <Card size="sm">
            <CardContent className="space-y-2 text-xs">
              <div className="flex items-center justify-between">
                <span className="font-medium">Location sharing</span>
                <Badge variant={fix ? "default" : "secondary"}>
                  {fix ? (fix.source === "simulated" ? "Simulated" : "Live") : "Off"}
                </Badge>
              </div>
              {permission === "denied" ? (
                <p className="text-destructive" role="alert">
                  Location permission denied. Re-grant access in your browser to broadcast your
                  position.
                </p>
              ) : permission === "prompt" || permission === "unknown" ? (
                <p className="text-muted-foreground">
                  Your browser will ask for location access on first broadcast.
                </p>
              ) : null}
              {geoError ? (
                <p className="text-destructive" role="alert">
                  {geoError}
                </p>
              ) : null}
              <div className="flex justify-end">
                <Button
                  type="button"
                  size="xs"
                  variant={useSimulated ? "secondary" : "ghost"}
                  onClick={() => setUseSimulated((s) => !s)}
                  data-testid="toggle-simulated"
                >
                  {useSimulated ? "Stop simulation" : "Use simulated position"}
                </Button>
              </div>
            </CardContent>
          </Card>

          <Separator />

          {driverRoute.stops.length === 0 ? (
            <p className="text-muted-foreground text-xs">No pickup stops on this route.</p>
          ) : (
            <ul className="space-y-2" data-testid="driver-manifest">
              {driverRoute.stops.map((stop, idx) => {
                const stopKey = stop.candidateNodeId ?? `stop-${idx}`;
                return (
                  <StopRow
                    key={stopKey}
                    index={idx}
                    stop={stop}
                    passengerNames={passengerNames}
                    eta={new Date(stop.arriveAt)}
                    arrived={Boolean(arrivedStops[stopKey])}
                    onArrive={() => onArrive(stopKey, stop.passengerIds)}
                    busy={busy}
                  />
                );
              })}
            </ul>
          )}
        </div>
      </aside>
    </div>
  );
}

function EmptyState({
  title,
  body,
  tripId,
}: {
  title: string;
  body: string;
  tripId: string;
}): React.JSX.Element {
  return (
    <div className="mx-auto max-w-md p-10">
      <Card>
        <CardHeader>
          <CardTitle>{title}</CardTitle>
        </CardHeader>
        <CardContent className="space-y-3 text-sm">
          <p className="text-muted-foreground">{body}</p>
          <Button
            variant="outline"
            size="sm"
            render={<Link href={`/trips/${tripId}`} />}
            nativeButton={false}
          >
            ← Back to trip overview
          </Button>
        </CardContent>
      </Card>
    </div>
  );
}
