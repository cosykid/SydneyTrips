"use client";

import { useRouter } from "next/navigation";
import { useForm, useWatch } from "react-hook-form";
import { zodResolver } from "@hookform/resolvers/zod";
import { z } from "zod";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Card, CardContent } from "@/components/ui/card";
import { useCreateTrip } from "@/lib/api/hooks";
import { GeocodePreview } from "./GeocodePreview";

const schema = z.object({
  name: z.string().min(2, "Give the trip a short name"),
  destinationAddress: z.string().min(3, "Where are we going?"),
  departAt: z
    .string()
    .min(1, "Pick a departure time")
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
  const form = useForm<FormValues, unknown, FormOutput>({
    resolver: zodResolver(schema),
    defaultValues: {
      name: "",
      destinationAddress: "",
      departAt: defaultDepartAt(),
      arrivalWindowMinutes: 15,
    },
  });

  const destinationAddress = useWatch({ control: form.control, name: "destinationAddress" });

  async function onSubmit(values: FormOutput) {
    try {
      const trip = await create.mutateAsync({
        name: values.name,
        destinationAddress: values.destinationAddress,
        departAt: new Date(values.departAt).toISOString(),
        arrivalWindowMinutes: values.arrivalWindowMinutes,
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
              <Label htmlFor="destinationAddress">Destination address</Label>
              <Input
                id="destinationAddress"
                placeholder="Palm Beach NSW 2108"
                {...form.register("destinationAddress")}
              />
              {form.formState.errors.destinationAddress ? (
                <p className="text-destructive text-xs">
                  {form.formState.errors.destinationAddress.message}
                </p>
              ) : null}
            </div>
            <div className="space-y-1.5">
              <Label htmlFor="departAt">Depart at</Label>
              <Input id="departAt" type="datetime-local" {...form.register("departAt")} />
              {form.formState.errors.departAt ? (
                <p className="text-destructive text-xs">{form.formState.errors.departAt.message}</p>
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
                +/- minutes of slack the optimiser can use around your target.
              </p>
            </div>
          </div>
          <GeocodePreview address={destinationAddress} />
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

function defaultDepartAt(): string {
  const d = new Date();
  d.setDate(d.getDate() + 1);
  d.setHours(9, 0, 0, 0);
  // datetime-local wants YYYY-MM-DDTHH:mm (local time, no TZ suffix)
  const pad = (n: number) => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}
