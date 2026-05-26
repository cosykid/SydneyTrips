"use client";

import { useState, type FormEvent } from "react";
import { Trash2, UserPlus } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Badge } from "@/components/ui/badge";
import { Select, SelectContent, SelectItem, SelectTrigger, SelectValue } from "@/components/ui/select";
import { useAddParticipant, useRemoveParticipant } from "@/lib/api/hooks";
import type { LatLng, Participant, ParticipantRole } from "@/lib/api/schema";
import { PlaceAutocomplete, type SelectedPlace } from "./PlaceAutocomplete";

interface ParticipantListProps {
  tripId: string;
  participants: Participant[];
}

export function ParticipantList({ tripId, participants }: ParticipantListProps): React.JSX.Element {
  const add = useAddParticipant();
  const remove = useRemoveParticipant();
  const [form, setForm] = useState({
    displayName: "",
    originAddress: "",
    role: "passenger" as ParticipantRole,
    seatsAvailable: 4,
  });
  // Captured when the user picks a suggestion from the Places dropdown —
  // gives us exact coordinates, skipping the backend's geocode hop.
  const [originLocation, setOriginLocation] = useState<LatLng | null>(null);
  // Google's `PlaceAutocompleteElement` is a web component that manages its
  // own internal input value — our `value` prop only drives the no-key
  // fallback render, so resetting form state doesn't clear the visible text.
  // Bumping this key after a successful add unmounts and recreates the
  // element with an empty input, which is what the user expects.
  const [originResetKey, setOriginResetKey] = useState(0);

  async function onAdd(e: FormEvent<HTMLFormElement>) {
    e.preventDefault();
    if (!form.displayName.trim() || !form.originAddress.trim()) return;
    try {
      await add.mutateAsync({
        tripId,
        body: {
          displayName: form.displayName.trim(),
          originAddress: form.originAddress.trim(),
          role: form.role,
          seatsAvailable: form.role === "driver" ? form.seatsAvailable : undefined,
          ...(originLocation ? { origin: originLocation } : {}),
        },
      });
      toast.success(`Added ${form.displayName}`);
      setForm({ displayName: "", originAddress: "", role: "passenger", seatsAvailable: 4 });
      setOriginLocation(null);
      setOriginResetKey((k) => k + 1);
    } catch (err) {
      toast.error("Could not add participant", {
        description: err instanceof Error ? err.message : undefined,
      });
    }
  }

  async function onRemove(participantId: string) {
    try {
      await remove.mutateAsync({ tripId, participantId });
    } catch (err) {
      toast.error("Could not remove participant", {
        description: err instanceof Error ? err.message : undefined,
      });
    }
  }

  return (
    <section className="space-y-3">
      <p className="text-muted-foreground text-[11px] font-medium tracking-wider uppercase">
        Participants · {participants.length}
      </p>

      <ul className="divide-border divide-y">
        {participants.length === 0 ? (
          <li className="text-muted-foreground py-3 text-sm">
            No participants yet — add the first one below.
          </li>
        ) : null}
        {participants.map((p) => (
          <li key={p.id} className="flex items-center justify-between gap-3 py-2.5 text-sm">
            <div className="min-w-0">
              <div className="flex items-center gap-2">
                <span className="text-foreground font-medium">{p.displayName}</span>
                <Badge variant={p.role === "driver" ? "default" : "secondary"}>
                  {p.role}
                  {p.role === "driver" && p.seatsAvailable ? ` · ${p.seatsAvailable} seats` : ""}
                </Badge>
              </div>
              <p className="text-muted-foreground truncate text-xs">{p.originAddress}</p>
            </div>
            <Button
              type="button"
              variant="ghost"
              size="icon-sm"
              onClick={() => onRemove(p.id)}
              disabled={remove.isPending}
              aria-label={`Remove ${p.displayName}`}
            >
              <Trash2 className="h-4 w-4" />
            </Button>
          </li>
        ))}
      </ul>

      <form onSubmit={onAdd} className="bg-secondary/60 space-y-3 rounded-lg p-3">
        <div className="space-y-1">
          <Label htmlFor="p-name">Name</Label>
          <Input
            id="p-name"
            className="bg-card"
            value={form.displayName}
            onChange={(e) => setForm((f) => ({ ...f, displayName: e.target.value }))}
            required
          />
        </div>
        <div className="space-y-1">
          <Label htmlFor="p-origin">Where they&apos;re starting from</Label>
          <PlaceAutocomplete
            key={originResetKey}
            id="p-origin"
            placeholder="123 Glebe Point Rd, Glebe"
            value={form.originAddress}
            onChange={(next) => {
              setForm((f) => ({ ...f, originAddress: next }));
              setOriginLocation(null);
            }}
            onPlace={(place: SelectedPlace) => {
              setForm((f) => ({ ...f, originAddress: place.address }));
              setOriginLocation(place.location);
            }}
          />
        </div>
        <div className="grid grid-cols-2 gap-3">
          <div className="space-y-1">
            <Label htmlFor="p-role">Role</Label>
            <Select
              value={form.role}
              onValueChange={(value) => setForm((f) => ({ ...f, role: value as ParticipantRole }))}
            >
              <SelectTrigger id="p-role" className="bg-card">
                <SelectValue />
              </SelectTrigger>
              <SelectContent>
                <SelectItem value="passenger">Passenger</SelectItem>
                <SelectItem value="driver">Driver</SelectItem>
              </SelectContent>
            </Select>
          </div>
          <div className="space-y-1">
            <Label htmlFor="p-seats">Seats</Label>
            <Input
              id="p-seats"
              type="number"
              min={1}
              max={8}
              className="bg-card"
              value={form.seatsAvailable}
              onChange={(e) =>
                setForm((f) => ({ ...f, seatsAvailable: Number(e.target.value) || 0 }))
              }
              disabled={form.role !== "driver"}
            />
          </div>
        </div>
        <Button type="submit" className="w-full" disabled={add.isPending}>
          <UserPlus className="h-4 w-4" />
          {add.isPending ? "Adding…" : "Add participant"}
        </Button>
      </form>
    </section>
  );
}
