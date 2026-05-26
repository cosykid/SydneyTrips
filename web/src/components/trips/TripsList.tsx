"use client";

import Link from "next/link";
import { useEffect, useMemo, useState } from "react";
import { format } from "date-fns";
import { Compass, Loader2, MapPin, Plus, Search } from "lucide-react";
import { useTrips } from "@/lib/api/hooks";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Separator } from "@/components/ui/separator";
import { useMapBackdrop, type MapPin as Pin } from "@/lib/mapStore";
import { SYDNEY_CBD } from "@/lib/store";
import type { TripStatus, TripSummary } from "@/lib/api/schema";

function statusLabel(status: TripStatus, hasLockedSolution: boolean): string {
  if (hasLockedSolution) return "Ready to go";
  switch (status) {
    case "draft":
      return "Not planned";
    case "planned":
      return "Ready to plan";
    case "in_progress":
      return "On the way";
    case "completed":
      return "Done";
    default:
      return status;
  }
}

export function TripsList(): React.JSX.Element {
  const trips = useTrips();
  const [query, setQuery] = useState("");
  const setFocus = useMapBackdrop((s) => s.setFocus);
  const clear = useMapBackdrop((s) => s.clear);

  const list = useMemo(() => trips.data ?? [], [trips.data]);

  // Drop a pin on the map for every trip's destination, framed on metro Sydney.
  useEffect(() => {
    const pins: Pin[] = list.map((t) => ({
      id: `trip-${t.id}`,
      position: t.destination,
      label: t.name,
      kind: "destination",
    }));
    setFocus({
      pins,
      center: { lat: SYDNEY_CBD.latitude, lng: SYDNEY_CBD.longitude },
      zoom: SYDNEY_CBD.zoom,
    });
    return () => clear();
  }, [list, setFocus, clear]);

  const filtered = useMemo(() => {
    const q = query.trim().toLowerCase();
    if (!q) return list;
    return list.filter(
      (t) =>
        t.name.toLowerCase().includes(q) ||
        t.destinationAddress.toLowerCase().includes(q),
    );
  }, [list, query]);

  return (
    <Card variant="floating" className="gap-0 overflow-hidden py-0">
      <div className="flex items-center gap-3 px-4 pt-4 pb-3">
        <span className="bg-accent text-accent-foreground flex h-9 w-9 items-center justify-center rounded-full">
          <Compass className="h-5 w-5" />
        </span>
        <div className="min-w-0">
          <p className="text-foreground text-[15px] leading-tight font-medium">SydneyTrips</p>
          <p className="text-muted-foreground text-xs">
            {list.length} {list.length === 1 ? "trip" : "trips"} around Sydney
          </p>
        </div>
      </div>

      <div className="px-4 pb-3">
        <label className="border-border focus-within:border-primary focus-within:ring-primary/20 flex h-10 items-center gap-2.5 rounded-full border px-3.5 transition-colors focus-within:ring-2">
          <Search className="text-muted-foreground h-4 w-4 shrink-0" />
          <input
            type="search"
            value={query}
            onChange={(e) => setQuery(e.target.value)}
            placeholder="Search your trips"
            aria-label="Search your trips"
            className="placeholder:text-muted-foreground w-full bg-transparent text-sm outline-none"
          />
        </label>
      </div>

      <Separator />

      {trips.isLoading ? (
        <div className="text-muted-foreground flex items-center gap-2 px-4 py-8 text-sm">
          <Loader2 className="h-4 w-4 animate-spin" /> Loading trips…
        </div>
      ) : trips.isError ? (
        <div className="px-4 py-6 text-sm">
          <p className="text-destructive font-medium">Could not load trips.</p>
          <p className="text-muted-foreground text-xs">{trips.error?.message}</p>
        </div>
      ) : list.length === 0 ? (
        <div className="flex flex-col items-center gap-3 px-6 py-10 text-center">
          <span className="bg-secondary text-muted-foreground flex h-12 w-12 items-center justify-center rounded-full">
            <MapPin className="h-6 w-6" />
          </span>
          <div>
            <p className="text-foreground text-sm font-medium">No trips yet</p>
            <p className="text-muted-foreground text-xs">
              Pick a destination and we&apos;ll figure out the pickups.
            </p>
          </div>
        </div>
      ) : filtered.length === 0 ? (
        <p className="text-muted-foreground px-4 py-8 text-center text-sm">
          No trips match “{query}”.
        </p>
      ) : (
        <ul className="max-h-[46vh] overflow-y-auto">
          {filtered.map((trip) => (
            <TripRow key={trip.id} trip={trip} />
          ))}
        </ul>
      )}

      <Separator />

      <div className="p-3">
        <Button
          className="w-full"
          render={<Link href="/trips/new" />}
          nativeButton={false}
        >
          <Plus className="h-4 w-4" />
          Create a trip
        </Button>
      </div>
    </Card>
  );
}

function TripRow({ trip }: { trip: TripSummary }): React.JSX.Element {
  const label = statusLabel(trip.status, trip.hasLockedSolution);
  const ready = trip.hasLockedSolution;
  return (
    <li>
      <Link
        href={`/trips/${trip.id}`}
        className="hover:bg-secondary flex items-center gap-3 px-4 py-2.5 transition-colors"
      >
        <span className="bg-secondary text-muted-foreground flex h-9 w-9 shrink-0 items-center justify-center rounded-full">
          <MapPin className="h-4 w-4" />
        </span>
        <div className="min-w-0 flex-1">
          <p className="text-foreground truncate text-sm font-medium">{trip.name}</p>
          <p className="text-muted-foreground truncate text-xs">{trip.destinationAddress}</p>
        </div>
        <div className="flex flex-col items-end gap-0.5 text-right">
          <span
            className={
              "flex items-center gap-1 text-[11px] font-medium " +
              (ready ? "text-success" : "text-muted-foreground")
            }
          >
            <span
              className={"h-1.5 w-1.5 rounded-full " + (ready ? "bg-success" : "bg-muted-foreground/50")}
            />
            {label}
          </span>
          <span className="text-muted-foreground text-[11px]">
            {format(new Date(trip.arriveBy), "d MMM")} · {trip.participantCount} ppl
          </span>
        </div>
      </Link>
    </li>
  );
}
