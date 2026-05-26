import type { Metadata } from "next";
import { PassengerView } from "@/components/live/PassengerView";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";

export const metadata: Metadata = { title: "Passenger · SydneyTrips" };

interface PageParams {
  params: Promise<{ id: string }>;
  searchParams: Promise<{ as?: string }>;
}

export default async function PassengerPage({
  params,
  searchParams,
}: PageParams): Promise<React.JSX.Element> {
  const { id } = await params;
  const { as } = await searchParams;

  if (!as) {
    return (
      <div className="pointer-events-auto mx-auto max-w-md p-10">
        <Card variant="floating">
          <CardHeader>
            <CardTitle>Pass through a participant id</CardTitle>
          </CardHeader>
          <CardContent className="text-muted-foreground space-y-2 text-sm">
            <p>
              Open this page with{" "}
              <code className="bg-muted rounded px-1">?as=&lt;participantId&gt;</code> so we know
              which pickup to show.
            </p>
            <p className="text-xs">
              Tip — drivers should head to <code className="bg-muted rounded px-1">/driver</code>{" "}
              instead.
            </p>
          </CardContent>
        </Card>
      </div>
    );
  }
  return (
    <div className="pointer-events-auto h-full w-full">
      <PassengerView tripId={id} participantId={as} />
    </div>
  );
}
