"use client";

import * as React from "react";
import { Popover as PopoverPrimitive } from "@base-ui/react/popover";

import { cn } from "@/lib/utils";

function Popover({ ...props }: PopoverPrimitive.Root.Props): React.JSX.Element {
  return <PopoverPrimitive.Root data-slot="popover" {...props} />;
}

function PopoverTrigger({
  ...props
}: PopoverPrimitive.Trigger.Props): React.JSX.Element {
  return <PopoverPrimitive.Trigger data-slot="popover-trigger" {...props} />;
}

function PopoverContent({
  className,
  align = "start",
  sideOffset = 6,
  children,
  ...props
}: PopoverPrimitive.Popup.Props & {
  align?: PopoverPrimitive.Positioner.Props["align"];
  sideOffset?: number;
}): React.JSX.Element {
  // The picker triggers live inside an OverlayPanel whose Card uses
  // `overflow-y-auto` so its contents scroll internally. base-ui defaults
  // `collisionBoundary` to `'clipping-ancestors'`, which would constrain the
  // popup to that scrolling box and push the dropdown into a corner. We
  // anchor against the viewport (document body) so the popup just hangs
  // under the trigger like Google Maps does. Popover content only renders
  // post-hydration (after a user click), so `document` is always defined
  // by the time this state is read — but we guard for SSR anyway.
  const [boundary] = React.useState<HTMLElement | null>(() =>
    typeof document !== "undefined" ? document.body : null,
  );

  return (
    <PopoverPrimitive.Portal>
      <PopoverPrimitive.Positioner
        align={align}
        sideOffset={sideOffset}
        collisionBoundary={boundary ?? "clipping-ancestors"}
      >
        <PopoverPrimitive.Popup
          data-slot="popover-content"
          className={cn(
            // Google's two-layer Material elevation + 8px radius is the
            // exact recipe Maps uses for the date/time picker surfaces.
            "z-50 rounded-lg bg-popover text-sm text-popover-foreground shadow-google-md outline-none",
            "data-open:animate-in data-open:fade-in-0 data-open:zoom-in-95",
            "data-closed:animate-out data-closed:fade-out-0 data-closed:zoom-out-95",
            className,
          )}
          {...props}
        >
          {children}
        </PopoverPrimitive.Popup>
      </PopoverPrimitive.Positioner>
    </PopoverPrimitive.Portal>
  );
}

export { Popover, PopoverTrigger, PopoverContent };
