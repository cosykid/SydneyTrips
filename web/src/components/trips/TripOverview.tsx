"use client";

import { useEffect, useState } from "react";
import Link from "next/link";
import { format } from "date-fns";
import {
  ArrowLeft,
  CalendarPlus,
  CarFront,
  Coins,
  Loader2,
  MapPin,
  Pencil,
  Route,
  User,
} from "lucide-react";
import { toast } from "sonner";
import { useTrip, useUpdateTripDestination } from "@/lib/api/hooks";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import {
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Label } from "@/components/ui/label";
import { Separator } from "@/components/ui/separator";
import { MapsAction } from "@/components/ui/maps-action";
import { ParticipantList } from "./ParticipantList";
import { PlaceAutocomplete, type SelectedPlace } from "./PlaceAutocomplete";
import { useMapBackdrop, type MapPin as Pin } from "@/lib/mapStore";
import { SYDNEY_CBD } from "@/lib/store";
import type { LatLng, TripStatus } from "@/lib/api/schema";

function statusLabel(status: TripStatus): string {
  switch (status) {
    case "draft":
      return "Draft";
    case "planned":
      return "Planned";
    case "in_progress":
      return "On the way";
    case "completed":
      return "Done";
    default:
      return status;
  }
}

/** Centre + zoom that frames a set of points around Sydney. */
function frame(points: LatLng[]): { center: LatLng; zoom: number } {
  if (points.length === 0) {
    return { center: { lat: SYDNEY_CBD.latitude, lng: SYDNEY_CBD.longitude }, zoom: SYDNEY_CBD.zoom };
  }
  const lats = points.map((p) => p.lat);
  const lngs = points.map((p) => p.lng);
  const minLat = Math.min(...lats);
  const maxLat = Math.max(...lats);
  const minLng = Math.min(...lngs);
  const maxLng = Math.max(...lngs);
  const span = Math.max(maxLat - minLat, maxLng - minLng);
  const zoom = span < 0.03 ? 13 : span < 0.08 ? 12 : span < 0.2 ? 11 : 10;
  return { center: { lat: (minLat + maxLat) / 2, lng: (minLng + maxLng) / 2 }, zoom };
}

export function TripOverview({ tripId }: { tripId: string }): React.JSX.Element {
  const trip = useTrip(tripId);
  const [editOpen, setEditOpen] = useState(false);
  const setFocus = useMapBackdrop((s) => s.setFocus);
  const clear = useMapBackdrop((s) => s.clear);

  const t = trip.data;

  // Plot the destination + everyone's home on the backdrop and frame them.
  useEffect(() => {
    if (!t) return;
    const pins: Pin[] = [
      { id: "dest", position: t.destination, label: t.destinationAddress, kind: "destination" },
      ...t.participants.map((p) => ({
        id: `p-${p.id}`,
        position: p.origin,
        label: p.displayName,
        kind: (p.role === "driver" ? "driver" : "home") as Pin["kind"],
      })),
    ];
    const { center, zoom } = frame(pins.map((p) => p.position));
    setFocus({ pins, center, zoom });
    return () => clear();
  }, [t, setFocus, clear]);

  if (trip.isLoading) {
    return (
      <Card variant="floating" className="px-5 py-4">
        <div className="text-muted-foreground flex items-center gap-2 text-sm">
          <Loader2 className="h-4 w-4 animate-spin" /> Loading trip…
        </div>
      </Card>
    );
  }
  if (trip.isError || !t) {
    return (
      <Card variant="floating" className="px-5 py-4 text-sm">
        <p className="text-destructive font-medium">Could not load trip.</p>
        <p className="text-muted-foreground text-xs">{trip.error?.message}</p>
        <Button
          variant="outline"
          size="sm"
          className="mt-3 w-fit"
          render={<Link href="/trips" />}
          nativeButton={false}
        >
          <ArrowLeft className="h-3.5 w-3.5" /> Your trips
        </Button>
      </Card>
    );
  }

  return (
    <Card variant="floating" className="max-h-[calc(100vh-2rem)] gap-0 overflow-y-auto py-0">
      <div className="px-5 pt-4 pb-3">
        <Link
          href="/trips"
          className="text-muted-foreground hover:text-foreground inline-flex items-center gap-1 text-xs"
        >
          <ArrowLeft className="h-3 w-3" /> Your trips
        </Link>
        <div className="mt-1.5 flex items-start justify-between gap-3">
          <h1 className="text-foreground text-xl leading-tight font-medium tracking-tight">
            {t.name}
          </h1>
          <span
            className={
              "mt-1 flex shrink-0 items-center gap-1.5 text-[12px] font-medium " +
              (t.hasLockedSolution ? "text-success" : "text-muted-foreground")
            }
          >
            <span
              className={
                "h-1.5 w-1.5 rounded-full " +
                (t.hasLockedSolution ? "bg-success" : "bg-muted-foreground/50")
              }
            />
            {t.hasLockedSolution ? "Ready to go" : "Not planned"}
          </span>
        </div>
        <p className="text-muted-foreground mt-1 flex items-center gap-1.5 text-sm">
          <MapPin className="h-3.5 w-3.5 shrink-0" />
          <span className="truncate">{t.destinationAddress}</span>
          <button
            type="button"
            onClick={() => setEditOpen(true)}
            className="text-primary hover:text-accent-foreground -my-1 inline-flex shrink-0 items-center gap-0.5 rounded px-1 py-0.5 text-[11px] font-medium"
            aria-label="Edit destination"
          >
            <Pencil className="h-3 w-3" /> Edit
          </button>
        </p>

        <dl className="mt-3 grid grid-cols-2 gap-3 text-sm">
          <Meta label="Arrive by" value={format(new Date(t.arriveBy), "EEE d MMM, HH:mm")} />
          <Meta label="People" value={String(t.participants.length)} />
          <Meta label="Status" value={statusLabel(t.status)} />
        </dl>
      </div>

      <div className="flex items-start justify-between gap-1 px-4 pb-4">
        <MapsAction icon={Route} label="Planner" href={`/trips/${tripId}/plan`} primary />
        <MapsAction icon={CarFront} label="Driver" href={`/trips/${tripId}/driver`} />
        <MapsAction icon={User} label="Passenger" href={`/trips/${tripId}/passenger`} />
        <MapsAction icon={Coins} label="Cost split" href={`/trips/${tripId}/cost`} />
      </div>

      {t.hasLockedSolution ? (
        <div className="border-border border-t px-5 py-3">
          <p className="text-muted-foreground mb-2 text-[11px] font-medium tracking-wider uppercase">
            Calendar holds
          </p>
          <div className="flex flex-wrap gap-1.5">
            {t.participants.map((p) => (
              <Button
                key={p.id}
                variant="outline"
                size="xs"
                render={
                  <a
                    href={`/api/proxy/trips/${tripId}/participants/${p.id}/calendar.ics`}
                    download={`${t.name}-${p.displayName}.ics`}
                  />
                }
                nativeButton={false}
              >
                <CalendarPlus className="h-3 w-3" />
                {p.displayName}
              </Button>
            ))}
          </div>
        </div>
      ) : null}

      <Separator />
      <div className="px-5 py-4">
        <ParticipantList tripId={tripId} participants={t.participants} />
      </div>

      <EditDestinationDialog
        tripId={tripId}
        open={editOpen}
        onOpenChange={setEditOpen}
        currentAddress={t.destinationAddress}
      />
    </Card>
  );
}

function Meta({ label, value }: { label: string; value: string }): React.JSX.Element {
  return (
    <div>
      <dt className="text-muted-foreground text-[10px] tracking-wider uppercase">{label}</dt>
      <dd className="text-foreground">{value}</dd>
    </div>
  );
}

interface EditDestinationDialogProps {
  tripId: string;
  open: boolean;
  onOpenChange: (open: boolean) => void;
  currentAddress: string;
}

function EditDestinationDialog({
  tripId,
  open,
  onOpenChange,
  currentAddress,
}: EditDestinationDialogProps): React.JSX.Element {
  const update = useUpdateTripDestination();
  const [address, setAddress] = useState(currentAddress);
  const [location, setLocation] = useState<LatLng | null>(null);

  async function onSave(): Promise<void> {
    if (!location) {
      toast.error("Pick a destination from the suggestions to confirm the location.");
      return;
    }
    try {
      await update.mutateAsync({
        tripId,
        destinationAddress: address,
        destination: location,
      });
      toast.success("Destination updated");
      onOpenChange(false);
    } catch (err) {
      toast.error("Could not update destination", {
        description: err instanceof Error ? err.message : undefined,
      });
    }
  }

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent>
        <DialogHeader>
          <DialogTitle>Change destination</DialogTitle>
          <DialogDescription>
            Search for the new address and pick a suggestion to confirm the exact location.
          </DialogDescription>
        </DialogHeader>
        <div className="space-y-1.5">
          <Label htmlFor="edit-destination">New destination</Label>
          <PlaceAutocomplete
            id="edit-destination"
            placeholder="Search for an address or place"
            value={address}
            onChange={(next) => {
              setAddress(next);
              setLocation(null);
            }}
            onPlace={(place: SelectedPlace) => {
              setAddress(place.address);
              setLocation(place.location);
            }}
          />
          {location ? (
            <p className="text-muted-foreground text-[11px]">
              Pinned at {location.lat.toFixed(4)}, {location.lng.toFixed(4)}
            </p>
          ) : (
            <p className="text-muted-foreground text-[11px]">Currently: {currentAddress}</p>
          )}
        </div>
        <DialogFooter>
          <DialogClose render={<Button variant="outline" />}>Cancel</DialogClose>
          <Button type="button" onClick={onSave} disabled={update.isPending || !location}>
            {update.isPending ? "Saving…" : "Save"}
          </Button>
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}
