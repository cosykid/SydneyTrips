import type { Metadata } from "next";
import { TripsList } from "@/components/trips/TripsList";
import { OverlayPanel } from "@/components/layout/OverlayPanel";

export const metadata: Metadata = { title: "Trips · SydneyTrips" };

export default function TripsIndexPage(): React.JSX.Element {
  return (
    <OverlayPanel>
      <TripsList />
    </OverlayPanel>
  );
}
