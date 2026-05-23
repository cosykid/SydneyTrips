"use client";

import { Loader2, PlugZap, Unplug, Wifi, WifiOff } from "lucide-react";
import type { ConnectionStatus } from "@/lib/realtime/hub";
import { cn } from "@/lib/utils";

export interface ConnectionBadgeProps {
  status: ConnectionStatus;
  error?: Error | null;
  className?: string;
}

const LABELS: Record<ConnectionStatus, string> = {
  idle: "Idle",
  connecting: "Connecting…",
  connected: "Live",
  reconnecting: "Reconnecting…",
  disconnected: "Offline",
};

export function ConnectionBadge({
  status,
  error,
  className,
}: ConnectionBadgeProps): React.JSX.Element {
  const Icon =
    status === "connected"
      ? Wifi
      : status === "connecting"
        ? Loader2
        : status === "reconnecting"
          ? PlugZap
          : status === "disconnected"
            ? error
              ? Unplug
              : WifiOff
            : WifiOff;
  return (
    <div
      role="status"
      aria-live="polite"
      title={error?.message}
      className={cn(
        "bg-background/90 flex items-center gap-1.5 rounded-md border px-2 py-1 text-[11px] font-medium shadow-sm",
        status === "connected" && "text-emerald-600 dark:text-emerald-400",
        status === "reconnecting" && "text-amber-600 dark:text-amber-400",
        status === "disconnected" && "text-muted-foreground",
        className,
      )}
    >
      <Icon
        className={cn(
          "h-3.5 w-3.5",
          (status === "connecting" || status === "reconnecting") && "animate-spin",
        )}
      />
      {LABELS[status]}
    </div>
  );
}
