"use client";

import Link from "next/link";
import { usePathname } from "next/navigation";
import {
  Compass,
  ListChecks,
  LogOut,
  Map,
  PlusCircle,
  UserCircle,
} from "lucide-react";
import clsx from "clsx";

const ROOT_LINKS = [
  { href: "/trips", label: "Trips", icon: ListChecks },
  { href: "/trips/new", label: "Create trip", icon: PlusCircle },
];

const PLACEHOLDER_LINKS = [
  { href: "/profile", label: "Profile", icon: UserCircle, disabled: true },
];

export interface SidebarProps {
  userName?: string;
  email?: string;
}

export function Sidebar({ userName, email }: SidebarProps): React.JSX.Element {
  const pathname = usePathname();
  return (
    <aside className="bg-sidebar text-sidebar-foreground border-sidebar-border flex h-screen w-60 flex-col border-r">
      <div className="flex items-center gap-2 px-5 py-6">
        <Compass className="text-sidebar-primary h-5 w-5" />
        <span className="text-base font-semibold tracking-tight">SydneyTrips</span>
      </div>
      <nav className="flex-1 space-y-1 px-3">
        {ROOT_LINKS.map((link) => {
          const Icon = link.icon;
          const active =
            link.href === pathname ||
            (link.href !== "/trips" && pathname.startsWith(link.href));
          return (
            <Link
              key={link.href}
              href={link.href}
              className={clsx(
                "flex items-center gap-3 rounded-md px-3 py-2 text-sm transition-colors",
                active
                  ? "bg-sidebar-accent text-sidebar-accent-foreground font-medium"
                  : "hover:bg-sidebar-accent/60",
              )}
            >
              <Icon className="h-4 w-4" />
              {link.label}
            </Link>
          );
        })}
        <div className="text-sidebar-foreground/60 px-3 pt-6 pb-1 text-xs uppercase tracking-wider">
          Coming soon
        </div>
        {PLACEHOLDER_LINKS.map((link) => {
          const Icon = link.icon;
          return (
            <span
              key={link.href}
              aria-disabled
              className="text-sidebar-foreground/40 flex cursor-not-allowed items-center gap-3 rounded-md px-3 py-2 text-sm"
            >
              <Icon className="h-4 w-4" />
              {link.label}
            </span>
          );
        })}
      </nav>
      <div className="border-sidebar-border space-y-2 border-t p-4">
        {userName ? (
          <div className="text-xs">
            <div className="font-medium">{userName}</div>
            {email ? <div className="text-sidebar-foreground/60">{email}</div> : null}
          </div>
        ) : null}
        <form action="/api/auth/logout" method="post">
          <button
            type="submit"
            className="text-sidebar-foreground/70 hover:text-sidebar-foreground flex w-full items-center gap-2 rounded-md px-2 py-1.5 text-xs"
          >
            <LogOut className="h-3.5 w-3.5" />
            Sign out
          </button>
        </form>
        <div className="text-sidebar-foreground/40 flex items-center gap-2 px-2 text-[10px] uppercase tracking-wider">
          <Map className="h-3 w-3" />
          Phase B preview
        </div>
      </div>
    </aside>
  );
}
