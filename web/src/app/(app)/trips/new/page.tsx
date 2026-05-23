import type { Metadata } from "next";
import { CreateTripForm } from "@/components/trips/CreateTripForm";

export const metadata: Metadata = { title: "New trip · SydneyTrips" };

export default function NewTripPage(): React.JSX.Element {
  return (
    <div className="mx-auto max-w-3xl space-y-6 p-8">
      <header>
        <h1 className="text-2xl font-semibold tracking-tight">Create a trip</h1>
        <p className="text-muted-foreground text-sm">
          Drop in a destination and timing window — we&apos;ll prep candidate pickup nodes when you
          add participants.
        </p>
      </header>
      <CreateTripForm />
    </div>
  );
}
