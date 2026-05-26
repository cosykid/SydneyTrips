"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { Compass, Plus } from "lucide-react";
import clsx from "clsx";

export function Sidebar(): React.JSX.Element {
  const pathname = usePathname() ?? "";
  // The compass *is* the Trips link — the old layout also had a separate
  // "Trips" list item pointing at the same /trips route, which was redundant.
  const onTrips = pathname === "/trips" || /^\/trips\/[^/]+/.test(pathname);
  const onCreate = pathname === "/trips/new";

  return (
    <aside className="bg-sidebar border-sidebar-border absolute top-0 left-0 z-30 flex h-screen w-14 flex-col items-center border-r py-3 shadow-[2px_0_8px_rgba(60,64,67,0.08)]">
      <Link
        href="/trips"
        title="SydneyTrips — your trips"
        aria-label="SydneyTrips — your trips"
        className={clsx(
          "mb-1 flex h-10 w-10 items-center justify-center rounded-full transition-colors",
          onTrips && !onCreate
            ? "bg-sidebar-accent text-sidebar-accent-foreground"
            : "text-sidebar-primary hover:bg-secondary",
        )}
      >
        <Compass className="h-5 w-5" />
      </Link>

      <nav className="flex flex-1 flex-col items-center gap-1 pt-2">
        <Link
          href="/trips/new"
          title="Create trip"
          aria-label="Create trip"
          className={clsx(
            "flex h-10 w-10 items-center justify-center rounded-full transition-colors",
            onCreate
              ? "bg-sidebar-accent text-sidebar-accent-foreground"
              : "text-sidebar-foreground/70 hover:bg-secondary hover:text-sidebar-foreground",
          )}
        >
          <Plus className="h-5 w-5" />
        </Link>
      </nav>
    </aside>
  );
}
