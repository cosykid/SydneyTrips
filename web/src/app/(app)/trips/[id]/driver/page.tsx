import type { Metadata } from "next";
import { DriverView } from "@/components/live/DriverView";

export const metadata: Metadata = { title: "Driver · SydneyTrips" };

interface PageParams {
  params: Promise<{ id: string }>;
  searchParams: Promise<{ as?: string }>;
}

export default async function DriverPage({
  params,
  searchParams,
}: PageParams): Promise<React.JSX.Element> {
  const { id } = await params;
  const { as } = await searchParams;
  return <DriverView tripId={id} currentUserParticipantId={as} />;
}
