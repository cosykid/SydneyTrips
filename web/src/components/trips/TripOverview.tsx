"use client";

import { format } from "date-fns";
import { Loader2, MapPin } from "lucide-react";
import { useTrip } from "@/lib/api/hooks";
import { Badge } from "@/components/ui/badge";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Separator } from "@/components/ui/separator";
import { ParticipantList } from "./ParticipantList";

export function TripOverview({ tripId }: { tripId: string }): React.JSX.Element {
  const trip = useTrip(tripId);

  if (trip.isLoading) {
    return (
      <div className="text-muted-foreground flex items-center gap-2 text-sm">
        <Loader2 className="h-4 w-4 animate-spin" /> Loading trip…
      </div>
    );
  }
  if (trip.isError || !trip.data) {
    return (
      <Card>
        <CardContent className="pt-6 text-sm">
          <p className="text-destructive">Could not load trip.</p>
          <p className="text-muted-foreground">{trip.error?.message}</p>
        </CardContent>
      </Card>
    );
  }

  const t = trip.data;

  return (
    <div className="space-y-6">
      <Card>
        <CardHeader className="flex flex-row items-start justify-between gap-2">
          <div>
            <CardTitle className="text-xl">{t.name}</CardTitle>
            <p className="text-muted-foreground flex items-center gap-1.5 text-sm">
              <MapPin className="h-3.5 w-3.5" /> {t.destinationAddress}
            </p>
          </div>
          <Badge variant={t.hasLockedSolution ? "default" : "secondary"}>
            {t.hasLockedSolution ? "Locked solution" : "No solution yet"}
          </Badge>
        </CardHeader>
        <CardContent>
          <dl className="grid grid-cols-2 gap-4 text-sm md:grid-cols-4">
            <div>
              <dt className="text-muted-foreground text-xs uppercase tracking-wider">Depart</dt>
              <dd>{format(new Date(t.departAt), "EEE d MMM, HH:mm")}</dd>
            </div>
            <div>
              <dt className="text-muted-foreground text-xs uppercase tracking-wider">Window</dt>
              <dd>+/- {t.arrivalWindowMinutes} min</dd>
            </div>
            <div>
              <dt className="text-muted-foreground text-xs uppercase tracking-wider">
                Participants
              </dt>
              <dd>{t.participants.length}</dd>
            </div>
            <div>
              <dt className="text-muted-foreground text-xs uppercase tracking-wider">Status</dt>
              <dd className="capitalize">{t.status.replace("_", " ")}</dd>
            </div>
          </dl>
          <Separator className="my-4" />
          <p className="text-muted-foreground text-xs">
            Open the planner to render origins + candidate pickup nodes on the map and trigger
            optimisation.
          </p>
        </CardContent>
      </Card>
      <ParticipantList tripId={tripId} participants={t.participants} />
    </div>
  );
}
