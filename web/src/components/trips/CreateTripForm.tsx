"use client";

import { useState } from "react";
import Link from "next/link";
import { useRouter } from "next/navigation";
import { Controller, useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { toast } from "sonner";
import { ArrowLeft } from "lucide-react";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Card } from "@/components/ui/card";
import { Separator } from "@/components/ui/separator";
import { DateTimePicker } from "@/components/ui/date-time-picker";
import { useCreateTrip } from "@/lib/api/hooks";
import { PlaceAutocomplete, type SelectedPlace } from "./PlaceAutocomplete";
import type { LatLng } from "@/lib/api/schema";

const schema = z.object({
  name: z.string().min(2, "Give the trip a short name"),
  destinationAddress: z.string().min(3, "Where are we going?"),
  arriveBy: z
    .string()
    .min(1, "Pick a time")
    .refine((value) => !Number.isNaN(new Date(value).getTime()), "Invalid datetime"),
});

type FormValues = z.input<typeof schema>;
type FormOutput = z.output<typeof schema>;

export function CreateTripForm(): React.JSX.Element {
  const router = useRouter();
  const create = useCreateTrip();
  // When the user picks a suggestion from Google Places we get an exact
  // lat/lng — passing it through saves the backend a geocoding hop and
  // means there's no ambiguity about which "Palm Beach" they meant.
  const [destinationLocation, setDestinationLocation] = useState<LatLng | null>(null);

  const form = useForm<FormValues, unknown, FormOutput>({
    resolver: zodResolver(schema),
    defaultValues: {
      name: "",
      destinationAddress: "",
      arriveBy: defaultArriveBy(),
    },
  });

  async function onSubmit(values: FormOutput): Promise<void> {
    try {
      const trip = await create.mutateAsync({
        name: values.name,
        destinationAddress: values.destinationAddress,
        arriveBy: new Date(values.arriveBy).toISOString(),
        ...(destinationLocation ? { destination: destinationLocation } : {}),
      });
      toast.success("Trip created");
      router.push(`/trips/${trip.id}`);
    } catch (error) {
      toast.error("Could not create trip", {
        description: error instanceof Error ? error.message : undefined,
      });
    }
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
        <h1 className="text-foreground mt-1.5 text-xl font-medium tracking-tight">Create a trip</h1>
        <p className="text-muted-foreground mt-0.5 text-xs">
          Drop in a destination and timing — we&apos;ll prep pickup points as you add people.
        </p>
      </div>
      <Separator />
      <form onSubmit={form.handleSubmit(onSubmit)} className="space-y-5 px-5 py-5">
        <div className="space-y-1.5">
          <Label htmlFor="name">Trip name</Label>
          <Input id="name" placeholder="Saturday at Palmy" {...form.register("name")} />
          {form.formState.errors.name ? (
            <p className="text-destructive text-xs">{form.formState.errors.name.message}</p>
          ) : null}
        </div>
        <div className="space-y-1.5">
          <Label htmlFor="destinationAddress">Where are you going?</Label>
          <Controller
            control={form.control}
            name="destinationAddress"
            render={({ field }) => (
              <PlaceAutocomplete
                id="destinationAddress"
                placeholder="Search for an address or place"
                value={field.value}
                onChange={(next) => {
                  field.onChange(next);
                  // Clear the resolved location when the user edits the
                  // text — it's no longer guaranteed to match.
                  setDestinationLocation(null);
                }}
                onPlace={(place: SelectedPlace) => {
                  field.onChange(place.address);
                  setDestinationLocation(place.location);
                }}
              />
            )}
          />
          {form.formState.errors.destinationAddress ? (
            <p className="text-destructive text-xs">
              {form.formState.errors.destinationAddress.message}
            </p>
          ) : null}
          {destinationLocation ? (
            <p className="text-muted-foreground text-[11px]">
              Pinned at {destinationLocation.lat.toFixed(4)},{" "}
              {destinationLocation.lng.toFixed(4)}
            </p>
          ) : null}
        </div>
        <div className="space-y-1.5">
          <Label htmlFor="arriveBy">Arrive by</Label>
          <Controller
            control={form.control}
            name="arriveBy"
            render={({ field }) => (
              <DateTimePicker id="arriveBy" value={field.value} onChange={field.onChange} />
            )}
          />
          {form.formState.errors.arriveBy ? (
            <p className="text-destructive text-xs">{form.formState.errors.arriveBy.message}</p>
          ) : null}
        </div>
        <div className="flex justify-end gap-2 pt-1">
          <Button type="button" variant="ghost" onClick={() => router.push("/trips")}>
            Cancel
          </Button>
          <Button type="submit" disabled={create.isPending}>
            {create.isPending ? "Creating…" : "Create trip"}
          </Button>
        </div>
      </form>
    </Card>
  );
}

function defaultArriveBy(): string {
  const d = new Date();
  d.setDate(d.getDate() + 1);
  d.setHours(10, 0, 0, 0);
  // datetime-local wants YYYY-MM-DDTHH:mm (local time, no TZ suffix)
  const pad = (n: number): string => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}
