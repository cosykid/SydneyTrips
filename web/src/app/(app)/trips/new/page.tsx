import type { Metadata } from "next";
import { CreateTripForm } from "@/components/trips/CreateTripForm";
import { OverlayPanel } from "@/components/layout/OverlayPanel";

export const metadata: Metadata = { title: "New trip · SydneyTrips" };

export default function NewTripPage(): React.JSX.Element {
  return (
    <OverlayPanel className="w-[420px]">
      <CreateTripForm />
    </OverlayPanel>
  );
}
