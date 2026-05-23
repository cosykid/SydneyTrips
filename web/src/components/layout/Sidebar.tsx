"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import { Compass, ListChecks, PlusCircle } from "lucide-react";
import clsx from "clsx";

const ROOT_LINKS = [
  { href: "/trips", label: "Trips", icon: ListChecks },
  { href: "/trips/new", label: "Create trip", icon: PlusCircle },
];

export function Sidebar(): React.JSX.Element {
  const pathname = usePathname();
  return (
    <aside className="bg-sidebar text-sidebar-foreground border-sidebar-border flex h-screen w-14 flex-col items-center border-r py-3">
      <Link
        href="/trips"
        title="SydneyTrips"
        className="text-sidebar-primary hover:bg-sidebar-accent/60 mb-2 flex h-10 w-10 items-center justify-center rounded-full transition-colors"
        aria-label="SydneyTrips home"
      >
        <Compass className="h-5 w-5" />
      </Link>
      <nav className="flex flex-1 flex-col items-center gap-1 pt-2">
        {ROOT_LINKS.map((link) => {
          const Icon = link.icon;
          const active =
            link.href === pathname ||
            (link.href !== "/trips" && pathname.startsWith(link.href));
          return (
            <Link
              key={link.href}
              href={link.href}
              title={link.label}
              aria-label={link.label}
              className={clsx(
                "flex h-10 w-10 items-center justify-center rounded-full transition-colors",
                active
                  ? "bg-sidebar-accent text-sidebar-accent-foreground"
                  : "text-sidebar-foreground/70 hover:bg-sidebar-accent/60 hover:text-sidebar-foreground",
              )}
            >
              <Icon className="h-5 w-5" />
            </Link>
          );
        })}
      </nav>
    </aside>
  );
}
