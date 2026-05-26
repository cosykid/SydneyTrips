import type { Metadata } from "next";
import { PlanCanvas } from "@/components/plan/PlanCanvas";

export const metadata: Metadata = { title: "Planner · SydneyTrips" };

interface Params {
  params: Promise<{ id: string }>;
}

export default async function PlanPage({ params }: Params): Promise<React.JSX.Element> {
  const { id } = await params;
  return (
    <div className="pointer-events-auto h-full w-full">
      <PlanCanvas tripId={id} />
    </div>
  );
}
