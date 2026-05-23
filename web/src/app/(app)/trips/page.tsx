import type { Metadata } from "next";
import { TripsList } from "@/components/trips/TripsList";

export const metadata: Metadata = { title: "Trips · SydneyTrips" };

export default function TripsIndexPage(): React.JSX.Element {
  return (
    <div className="mx-auto max-w-5xl space-y-6 p-8">
      <header className="flex items-baseline justify-between">
        <div>
          <h1 className="text-2xl font-semibold tracking-tight">Your trips</h1>
          <p className="text-muted-foreground text-sm">
            Plan multi-driver routes across Sydney with optimised pickups.
          </p>
        </div>
      </header>
      <TripsList />
    </div>
  );
}
