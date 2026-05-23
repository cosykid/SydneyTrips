"use client";

import { useState } from "react";
import { format } from "date-fns";
import { CalendarPlus, Loader2, MapPin, Pencil } from "lucide-react";
import { toast } from "sonner";
import { useTrip, useUpdateTripDestination } from "@/lib/api/hooks";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
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
import { ParticipantList } from "./ParticipantList";
import { PlaceAutocomplete, type SelectedPlace } from "./PlaceAutocomplete";
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

export function TripOverview({ tripId }: { tripId: string }): React.JSX.Element {
  const trip = useTrip(tripId);
  const [editOpen, setEditOpen] = useState(false);

  if (trip.isLoading) {
    return (
      <div className="text-muted-foreground flex items-center gap-2 text-sm">
        <Loader2 className="h-4 w-4 animate-spin" /> Loading trip…
      </div>
    );
  }
  if (trip.isError || !trip.data) {
    return (
      <Card>
        <CardContent className="pt-6 text-sm">
          <p className="text-destructive">Could not load trip.</p>
          <p className="text-muted-foreground">{trip.error?.message}</p>
        </CardContent>
      </Card>
    );
  }

  const t = trip.data;

  return (
    <div className="space-y-6">
      <Card>
        <CardHeader className="flex flex-row items-start justify-between gap-2">
          <div className="min-w-0">
            <CardTitle className="text-xl">{t.name}</CardTitle>
            <p className="text-muted-foreground flex items-center gap-1.5 text-sm">
              <MapPin className="h-3.5 w-3.5 flex-shrink-0" />
              <span className="truncate">{t.destinationAddress}</span>
              <button
                type="button"
                onClick={() => setEditOpen(true)}
                className="text-muted-foreground hover:text-foreground -my-1 inline-flex items-center gap-0.5 rounded px-1 py-0.5 text-[11px] hover:underline"
                aria-label="Edit destination"
              >
                <Pencil className="h-3 w-3" />
                Edit
              </button>
            </p>
          </div>
          <Badge variant={t.hasLockedSolution ? "default" : "secondary"}>
            {t.hasLockedSolution ? "Ready to go" : "Not planned yet"}
          </Badge>
        </CardHeader>
        <CardContent>
          <dl className="grid grid-cols-2 gap-4 text-sm md:grid-cols-4">
            <div>
              <dt className="text-muted-foreground text-[10px] uppercase tracking-wider">Arrive by</dt>
              <dd>{format(new Date(t.arriveBy), "EEE d MMM, HH:mm")}</dd>
            </div>
            <div>
              <dt className="text-muted-foreground text-[10px] uppercase tracking-wider">Arrival window</dt>
              <dd>+/- {t.arrivalWindowMinutes} min</dd>
            </div>
            <div>
              <dt className="text-muted-foreground text-[10px] uppercase tracking-wider">People</dt>
              <dd>{t.participants.length}</dd>
            </div>
            <div>
              <dt className="text-muted-foreground text-[10px] uppercase tracking-wider">Status</dt>
              <dd>{statusLabel(t.status)}</dd>
            </div>
          </dl>
          <Separator className="my-4" />
          {t.hasLockedSolution ? (
            <div className="flex flex-wrap items-center gap-2 text-xs">
              <span className="text-muted-foreground">Calendar holds:</span>
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
                  <CalendarPlus className="mr-1 h-3 w-3" />
                  {p.displayName}
                </Button>
              ))}
            </div>
          ) : (
            <p className="text-muted-foreground text-xs">
              Open the planner to find the best pickup points and routes.
            </p>
          )}
        </CardContent>
      </Card>

      <EditDestinationDialog
        tripId={tripId}
        open={editOpen}
        onOpenChange={setEditOpen}
        currentAddress={t.destinationAddress}
      />

      <ParticipantList tripId={tripId} participants={t.participants} />
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
            <p className="text-muted-foreground text-[11px]">
              Currently: {currentAddress}
            </p>
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
