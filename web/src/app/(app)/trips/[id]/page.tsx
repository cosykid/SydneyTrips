import type { Metadata } from "next";
import { TripOverview } from "@/components/trips/TripOverview";
import { OverlayPanel } from "@/components/layout/OverlayPanel";

export const metadata: Metadata = { title: "Trip · SydneyTrips" };

interface Params {
  params: Promise<{ id: string }>;
}

export default async function TripDetailPage({ params }: Params): Promise<React.JSX.Element> {
  const { id } = await params;
  return (
    <OverlayPanel className="w-[400px]">
      <TripOverview tripId={id} />
    </OverlayPanel>
  );
}
