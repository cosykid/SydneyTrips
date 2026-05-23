import type { Metadata } from "next";
import Link from "next/link";
import { Button } from "@/components/ui/button";
import { TripOverview } from "@/components/trips/TripOverview";

export const metadata: Metadata = { title: "Trip · SydneyTrips" };

interface Params {
  params: Promise<{ id: string }>;
}

export default async function TripDetailPage({ params }: Params): Promise<React.JSX.Element> {
  const { id } = await params;
  return (
    <div className="mx-auto max-w-5xl space-y-6 p-8">
      <div className="flex items-center justify-between">
        <Button variant="ghost" size="sm" render={<Link href="/trips" />} nativeButton={false}>
          ← All trips
        </Button>
        <div className="flex flex-wrap gap-2">
          <Button
            variant="outline"
            render={<Link href={`/trips/${id}/driver`} />}
            nativeButton={false}
          >
            Driver view
          </Button>
          <Button
            variant="outline"
            render={<Link href={`/trips/${id}/passenger`} />}
            nativeButton={false}
          >
            Passenger view
          </Button>
          <Button
            variant="outline"
            render={<Link href={`/trips/${id}/cost`} />}
            nativeButton={false}
          >
            Cost split
          </Button>
          <Button render={<Link href={`/trips/${id}/plan`} />} nativeButton={false}>
            Open planner
          </Button>
        </div>
      </div>
      <TripOverview tripId={id} />
    </div>
  );
}
