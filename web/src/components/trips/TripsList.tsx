"use client";

import Link from "next/link";
import { format } from "date-fns";
import { ArrowRight, Loader2, PlusCircle } from "lucide-react";
import { useTrips } from "@/lib/api/hooks";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Badge } from "@/components/ui/badge";

export function TripsList(): React.JSX.Element {
  const trips = useTrips();

  if (trips.isLoading) {
    return (
      <div className="text-muted-foreground flex items-center gap-2 text-sm">
        <Loader2 className="h-4 w-4 animate-spin" /> Loading trips…
      </div>
    );
  }

  if (trips.isError) {
    return (
      <Card>
        <CardContent className="pt-6 text-sm">
          <p className="text-destructive">Could not load trips.</p>
          <p className="text-muted-foreground">{trips.error?.message}</p>
        </CardContent>
      </Card>
    );
  }

  const list = trips.data ?? [];

  if (list.length === 0) {
    return (
      <Card className="border-dashed">
        <CardContent className="flex flex-col items-center gap-3 py-12 text-center">
          <PlusCircle className="text-muted-foreground h-8 w-8" />
          <div>
            <p className="font-medium">No trips yet</p>
            <p className="text-muted-foreground text-sm">
              Spin up your first plan — we&apos;ll handle the routing.
            </p>
          </div>
          <Button render={<Link href="/trips/new" />} nativeButton={false}>
            Create a trip
          </Button>
        </CardContent>
      </Card>
    );
  }

  return (
    <div className="grid gap-3 sm:grid-cols-2">
      {list.map((trip) => (
        <Card key={trip.id} className="hover:border-foreground/20 transition-colors">
          <CardHeader className="flex flex-row items-start justify-between gap-2">
            <div>
              <CardTitle className="text-base">{trip.name}</CardTitle>
              <p className="text-muted-foreground text-xs">{trip.destinationAddress}</p>
            </div>
            <Badge variant={trip.hasLockedSolution ? "default" : "secondary"}>
              {trip.hasLockedSolution ? "locked" : trip.status}
            </Badge>
          </CardHeader>
          <CardContent className="space-y-3">
            <dl className="text-muted-foreground grid grid-cols-2 gap-2 text-xs">
              <div>
                <dt className="uppercase tracking-wider">Depart</dt>
                <dd className="text-foreground">
                  {format(new Date(trip.departAt), "EEE d MMM, HH:mm")}
                </dd>
              </div>
              <div>
                <dt className="uppercase tracking-wider">Participants</dt>
                <dd className="text-foreground">{trip.participantCount}</dd>
              </div>
            </dl>
            <Button
              variant="outline"
              className="w-full justify-between"
              render={<Link href={`/trips/${trip.id}`} />}
              nativeButton={false}
            >
              <span>Open trip</span>
              <ArrowRight className="h-4 w-4" />
            </Button>
          </CardContent>
        </Card>
      ))}
      <Card className="border-dashed">
        <CardContent className="flex h-full items-center justify-center py-10">
          <Button
            variant="ghost"
            className="flex items-center gap-2"
            render={<Link href="/trips/new" />}
            nativeButton={false}
          >
            <PlusCircle className="h-4 w-4" /> New trip
          </Button>
        </CardContent>
      </Card>
    </div>
  );
}
