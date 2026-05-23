"use client";

// Passenger live view — top half map (pickup + driver position), bottom half
// pickup details, walk-time, "I'm here" button, live ETA.

import Link from "next/link";
import dynamic from "next/dynamic";
import { useEffect, useMemo, useRef, useState } from "react";
import { format, formatDistanceToNowStrict } from "date-fns";
import {
  ArrowLeft,
  CalendarPlus,
  CarFront,
  Footprints,
  Loader2,
  MapPin,
} from "lucide-react";
import { toast } from "sonner";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Separator } from "@/components/ui/separator";
import { ConnectionBadge } from "./ConnectionBadge";
import { fitBounds } from "./LiveMap";
import type { LiveMapProps } from "./LiveMap";
import { useLockedSolution, useTrip } from "@/lib/api/hooks";
import { useTripHub } from "@/lib/realtime/hub";
import { SYDNEY_CBD, type MapViewState } from "@/lib/store";
import type {
  DriverPositionPayload,
  EtaUpdatedPayload,
  RouteRecomputedPayload,
} from "@/lib/realtime/hub";
import type { LatLng, Solution, SolutionRoute, SolutionStop } from "@/lib/api/schema";

const LiveMap = dynamic<LiveMapProps>(() => import("./LiveMap").then((m) => m.LiveMap), {
  ssr: false,
  loading: () => (
    <div className="bg-muted/40 flex h-full w-full items-center justify-center">
      <Loader2 className="text-muted-foreground h-6 w-6 animate-spin" />
    </div>
  ),
});

export interface PassengerViewProps {
  tripId: string;
  participantId: string;
}

interface PickupInfo {
  route: SolutionRoute;
  stop: SolutionStop;
  index: number;
}

function findPickupForPassenger(
  solution: Solution | null | undefined,
  participantId: string,
): PickupInfo | undefined {
  if (!solution) return undefined;
  for (const route of solution.routes) {
    for (let i = 0; i < route.stops.length; i += 1) {
      if (route.stops[i].passengerIds.includes(participantId)) {
        return { route, stop: route.stops[i], index: i };
      }
    }
  }
  return undefined;
}

function approxWalkMinutes(from: LatLng | undefined, to: LatLng): number | null {
  if (!from) return null;
  // Haversine, then 80 m/min walking pace (~4.8 km/h).
  const R = 6371_000;
  const toRad = (n: number): number => (n * Math.PI) / 180;
  const dLat = toRad(to.lat - from.lat);
  const dLng = toRad(to.lng - from.lng);
  const a =
    Math.sin(dLat / 2) ** 2 +
    Math.cos(toRad(from.lat)) * Math.cos(toRad(to.lat)) * Math.sin(dLng / 2) ** 2;
  const c = 2 * Math.atan2(Math.sqrt(a), Math.sqrt(1 - a));
  const metres = R * c;
  return metres / 80;
}

export function PassengerView({
  tripId,
  participantId,
}: PassengerViewProps): React.JSX.Element {
  const trip = useTrip(tripId);
  const locked = useLockedSolution(tripId);
  const hub = useTripHub(tripId);
  const [driverPos, setDriverPos] = useState<LatLng | null>(null);
  const [liveEta, setLiveEta] = useState<Date | null>(null);
  const [imHere, setImHere] = useState(false);
  const [myPos, setMyPos] = useState<LatLng | undefined>(undefined);
  const arrivalAnnounceRef = useRef<number>(0);

  // Subscribe to hub events.
  useEffect(() => {
    const off1 = hub.onDriverPosition((p: DriverPositionPayload) => {
      setDriverPos({ lat: p.lat, lng: p.lng });
    });
    const off2 = hub.onEtaUpdated((p: EtaUpdatedPayload) => {
      if (p.passengerId !== participantId) return;
      setLiveEta(new Date(p.newEta));
    });
    const off3 = hub.onRouteRecomputed((p: RouteRecomputedPayload) => {
      toast.info("Route changed", {
        description: `Driver re-routed (${p.solutionId.slice(0, 8)}). Pickup may be different.`,
      });
    });
    return () => {
      off1();
      off2();
      off3();
    };
  }, [hub, participantId]);

  // One-off non-broadcasting geolocation read just so we can show walk-time.
  useEffect(() => {
    if (typeof navigator === "undefined" || !navigator.geolocation) return;
    navigator.geolocation.getCurrentPosition(
      (pos) => setMyPos({ lat: pos.coords.latitude, lng: pos.coords.longitude }),
      () => {
        /* swallow — walk-time becomes "unknown" */
      },
      { maximumAge: 60_000, timeout: 8000 },
    );
  }, []);

  const pickup = useMemo(
    () => findPickupForPassenger(locked.data, participantId),
    [locked.data, participantId],
  );
  const initialEta = pickup ? new Date(pickup.stop.arriveAt) : null;
  const currentEta = liveEta ?? initialEta;
  const driverName = pickup?.route.driverDisplayName;

  // Reverse: when the driver is "almost here" we want a one-shot toast.
  useEffect(() => {
    if (!currentEta) return;
    const minutes = (currentEta.getTime() - Date.now()) / 60_000;
    if (minutes < 2 && minutes > -1 && Date.now() - arrivalAnnounceRef.current > 60_000) {
      arrivalAnnounceRef.current = Date.now();
      toast.success(`${driverName ?? "Your driver"} is almost here`, {
        description: `Expected ${format(currentEta, "HH:mm")}`,
      });
    }
  }, [currentEta, driverName]);

  const fitPoints = useMemo<LatLng[]>(() => {
    const out: LatLng[] = [];
    if (pickup) out.push(pickup.stop.location);
    if (driverPos) out.push(driverPos);
    if (myPos) out.push(myPos);
    return out;
  }, [pickup, driverPos, myPos]);

  const fittedView = useMemo<MapViewState>(
    () => (fitPoints.length ? fitBounds(fitPoints, SYDNEY_CBD) : { ...SYDNEY_CBD }),
    [fitPoints],
  );
  const [userView, setUserView] = useState<MapViewState | null>(null);
  const viewState = userView ?? fittedView;
  const setViewState = (v: MapViewState): void => setUserView(v);

  if (trip.isLoading || locked.isLoading) {
    return (
      <div className="flex h-full items-center justify-center">
        <Loader2 className="text-muted-foreground h-6 w-6 animate-spin" />
      </div>
    );
  }

  if (!trip.data) {
    return <p className="p-8 text-sm">Trip not found.</p>;
  }

  if (!locked.data || !pickup) {
    return (
      <div className="mx-auto max-w-md p-10">
        <Card>
          <CardHeader>
            <CardTitle>No pickup assigned yet</CardTitle>
          </CardHeader>
          <CardContent className="space-y-3 text-sm">
            <p className="text-muted-foreground">
              You will see your live pickup once the trip organiser locks a solution and you are
              part of a driver&apos;s route.
            </p>
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

  const walkMinutes = approxWalkMinutes(myPos, pickup.stop.location);
  const walkHref = myPos
    ? `https://maps.google.com/?daddr=${pickup.stop.location.lat},${pickup.stop.location.lng}&saddr=${myPos.lat},${myPos.lng}&dirflg=w`
    : `https://maps.google.com/?daddr=${pickup.stop.location.lat},${pickup.stop.location.lng}&dirflg=w`;
  const calendarHref = `/api/proxy/trips/${tripId}/participants/${participantId}/calendar.ics`;

  async function onCheckIn(): Promise<void> {
    setImHere(true);
    const stopKey = pickup!.stop.candidateNodeId ?? `stop-${pickup!.index}`;
    try {
      await hub.passengerCheckIn(stopKey);
      toast.success("Checked in", {
        description: "Waiting for your driver",
      });
    } catch (err) {
      toast.error("Check-in failed", {
        description: err instanceof Error ? err.message : undefined,
      });
      setImHere(false);
    }
  }

  return (
    <div className="flex h-full w-full flex-col">
      <div className="relative h-[55%] min-h-0">
        <LiveMap
          destination={{
            address: trip.data.destinationAddress,
            point: trip.data.destination,
          }}
          route={pickup.route}
          highlightStopIndex={pickup.index}
          driverPosition={driverPos ?? undefined}
          participantHome={myPos}
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
      <div className="flex-1 overflow-y-auto">
        <div className="mx-auto max-w-2xl space-y-4 p-5">
          <Card>
            <CardHeader>
              <CardTitle className="flex items-center gap-2 text-lg">
                <MapPin className="text-primary h-4 w-4" /> Your pickup
              </CardTitle>
            </CardHeader>
            <CardContent className="space-y-2 text-sm">
              <p className="font-medium">
                Stop {pickup.index + 1} of {pickup.route.stops.length} on{" "}
                {pickup.route.driverDisplayName}&apos;s route
              </p>
              <p className="text-muted-foreground">
                {pickup.stop.candidateNodeId ? "Public-transport hub" : "Pickup point"}
              </p>
              <div className="flex flex-wrap items-center gap-3 pt-1 text-xs">
                <Badge variant="secondary">
                  <Footprints className="mr-1 h-3 w-3" />
                  {walkMinutes != null ? `${walkMinutes.toFixed(0)} min walk` : "Walk time unknown"}
                </Badge>
                <Badge variant="outline">
                  Pickup walk allowance {pickup.stop.walkMetres.toFixed(0)} m
                </Badge>
                <a
                  href={walkHref}
                  className="text-primary text-xs hover:underline"
                  target="_blank"
                  rel="noreferrer"
                >
                  Open walking directions
                </a>
              </div>
              <Separator className="my-3" />
              <dl className="grid grid-cols-2 gap-3 text-xs">
                <div>
                  <dt className="text-muted-foreground uppercase tracking-wider">Driver</dt>
                  <dd className="flex items-center gap-1.5 font-medium">
                    <CarFront className="h-3.5 w-3.5" /> {pickup.route.driverDisplayName}
                  </dd>
                </div>
                <div>
                  <dt className="text-muted-foreground uppercase tracking-wider">Scheduled</dt>
                  <dd className="font-medium tabular-nums">
                    {format(new Date(pickup.stop.arriveAt), "EEE HH:mm")}
                  </dd>
                </div>
                <div className="col-span-2">
                  <dt className="text-muted-foreground uppercase tracking-wider">Live ETA</dt>
                  <dd
                    aria-live="polite"
                    aria-atomic
                    className="text-foreground text-2xl font-semibold tabular-nums"
                    data-testid="live-eta"
                  >
                    {currentEta
                      ? `${format(currentEta, "HH:mm")} (${formatDistanceToNowStrict(currentEta, { addSuffix: true })})`
                      : "Awaiting first update…"}
                  </dd>
                </div>
              </dl>
            </CardContent>
          </Card>

          <Button
            type="button"
            className="h-12 w-full text-base"
            onClick={onCheckIn}
            disabled={imHere}
            data-testid="im-here-button"
          >
            {imHere ? "Waiting for driver…" : "I’m here"}
          </Button>

          <div className="flex justify-center">
            <Button
              variant="outline"
              size="sm"
              render={<a href={calendarHref} download={`trip-${tripId}.ics`} />}
              nativeButton={false}
            >
              <CalendarPlus className="mr-1.5 h-4 w-4" />
              Add to calendar
            </Button>
          </div>
        </div>
      </div>
    </div>
  );
}
