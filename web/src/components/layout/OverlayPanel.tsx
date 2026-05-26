import type { ReactNode } from "react";
import { cn } from "@/lib/utils";

/**
 * A floating overlay container for pages that sit on top of the persistent
 * Sydney map. Opts back into pointer events (the app `main` is transparent so
 * the map can be dragged in the gaps) and caps its height to the viewport so
 * its contents scroll internally rather than pushing the page.
 */
export function OverlayPanel({
  children,
  className,
}: {
  children: ReactNode;
  className?: string;
}): React.JSX.Element {
  return (
    <section
      className={cn(
        "pointer-events-auto absolute top-4 left-4 z-10 flex max-h-[calc(100vh-2rem)] w-[380px] max-w-[calc(100vw-5rem)] flex-col",
        className,
      )}
    >
      {children}
    </section>
  );
}
