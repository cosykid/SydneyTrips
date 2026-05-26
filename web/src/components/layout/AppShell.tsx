import type { ReactNode } from "react";
import { Sidebar } from "./Sidebar";
import { MapBackdrop } from "@/components/map/MapBackdrop";

/**
 * The Sydney map is the floor of the whole app. The rail sits on the left,
 * pages float on top as overlays. `main` is pointer-transparent so the map
 * stays draggable in the gaps between panels — each page opts its own panels
 * (and the map-owning routes their full map) back into pointer events.
 */
export function AppShell({ children }: { children: ReactNode }): React.JSX.Element {
  return (
    <div className="bg-background relative h-screen w-full overflow-hidden">
      <div className="absolute inset-0 z-0">
        <MapBackdrop />
      </div>
      <Sidebar />
      <main className="pointer-events-none absolute inset-y-0 right-0 left-14 z-10">
        {children}
      </main>
    </div>
  );
}
