import type { Metadata } from "next";
import { CostBreakdown } from "@/components/trips/CostBreakdown";
import { OverlayPanel } from "@/components/layout/OverlayPanel";

export const metadata: Metadata = { title: "Cost split · SydneyTrips" };

interface Params {
  params: Promise<{ id: string }>;
}

export default async function CostPage({ params }: Params): Promise<React.JSX.Element> {
  const { id } = await params;
  return (
    <OverlayPanel className="w-[420px]">
      <CostBreakdown tripId={id} />
    </OverlayPanel>
  );
}
