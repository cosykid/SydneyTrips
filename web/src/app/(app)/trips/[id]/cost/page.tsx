import type { Metadata } from "next";
import Link from "next/link";
import { Button } from "@/components/ui/button";
import { CostBreakdown } from "@/components/trips/CostBreakdown";

export const metadata: Metadata = { title: "Cost split · SydneyTrips" };

interface Params {
  params: Promise<{ id: string }>;
}

export default async function CostPage({ params }: Params): Promise<React.JSX.Element> {
  const { id } = await params;
  return (
    <div className="mx-auto max-w-4xl space-y-6 p-8">
      <div className="flex items-center justify-between">
        <Button
          variant="ghost"
          size="sm"
          render={<Link href={`/trips/${id}`} />}
          nativeButton={false}
        >
          ← Trip overview
        </Button>
      </div>
      <header>
        <h1 className="text-2xl font-semibold tracking-tight">Cost split</h1>
        <p className="text-muted-foreground text-sm">
          Fair fuel + tolls split by passenger-distance carried. Real numbers arrive once WS7 lands.
        </p>
      </header>
      <CostBreakdown tripId={id} />
    </div>
  );
}
