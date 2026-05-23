"use client";

import { useState } from "react";
import { useRouter } from "next/navigation";
import { Controller, useForm } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Card, CardContent } from "@/components/ui/card";
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
  arrivalWindowMinutes: z
    .number()
    .or(z.string())
    .transform((value) => Number(value))
    .pipe(z.number().int().min(0).max(240)),
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
      arrivalWindowMinutes: 15,
    },
  });

  async function onSubmit(values: FormOutput): Promise<void> {
    try {
      const trip = await create.mutateAsync({
        name: values.name,
        destinationAddress: values.destinationAddress,
        arriveBy: new Date(values.arriveBy).toISOString(),
        arrivalWindowMinutes: values.arrivalWindowMinutes,
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
    <Card>
      <CardContent className="space-y-6 pt-6">
        <form onSubmit={form.handleSubmit(onSubmit)} className="space-y-5">
          <div className="space-y-1.5">
            <Label htmlFor="name">Trip name</Label>
            <Input id="name" placeholder="Saturday at Palmy" {...form.register("name")} />
            {form.formState.errors.name ? (
              <p className="text-destructive text-xs">{form.formState.errors.name.message}</p>
            ) : null}
          </div>
          <div className="grid gap-5 sm:grid-cols-2">
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
              <Input id="arriveBy" type="datetime-local" {...form.register("arriveBy")} />
              {form.formState.errors.arriveBy ? (
                <p className="text-destructive text-xs">{form.formState.errors.arriveBy.message}</p>
              ) : null}
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="arrivalWindowMinutes">Arrival window (min)</Label>
              <Input
                id="arrivalWindowMinutes"
                type="number"
                min={0}
                max={240}
                {...form.register("arrivalWindowMinutes")}
              />
              <p className="text-muted-foreground text-xs">
                +/- minutes of flexibility around your target arrival time.
              </p>
            </div>
          </div>
          <div className="flex justify-end gap-2">
            <Button type="button" variant="ghost" onClick={() => router.push("/trips")}>
              Cancel
            </Button>
            <Button type="submit" disabled={create.isPending}>
              {create.isPending ? "Creating…" : "Create trip"}
            </Button>
          </div>
        </form>
      </CardContent>
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
