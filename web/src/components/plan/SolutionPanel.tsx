"use client";

import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import type { Solution } from "@/lib/api/schema";

export interface SolutionPanelProps {
  solution: Solution;
  onLock: () => void;
  onWhatIf?: (solution: Solution) => void;
  isLocking?: boolean;
  lockedSolutionId?: string;
}

/**
 * Single-solution view shown beneath the weight sliders. Replaces the old
 * three-tab Pareto carousel — there's now just one solution per run, driven
 * by the current slider values, and sliders re-solve via debounce. Locking
 * and what-if entry points still live here.
 */
export function SolutionPanel({
  solution,
  onLock,
  onWhatIf,
  isLocking,
  lockedSolutionId,
}: SolutionPanelProps): React.JSX.Element {
  const isLocked = lockedSolutionId === solution.id;

  return (
    <section className="space-y-3" data-testid="solution-panel">
      <h2 className="text-sm font-semibold">Plan</h2>
      <SolutionMetrics solution={solution} />
      <div className="flex flex-col gap-1.5">
        <Button
          type="button"
          className="w-full"
          onClick={() => onLock()}
          disabled={isLocking || isLocked}
        >
          {isLocked ? "In use" : isLocking ? "Saving…" : "Use this plan"}
        </Button>
        {onWhatIf && isLocked ? (
          <Button
            type="button"
            variant="outline"
            size="sm"
            className="w-full"
            onClick={() => onWhatIf(solution)}
            data-testid="what-if-button"
          >
            Try changes
          </Button>
        ) : null}
      </div>
    </section>
  );
}

function SolutionMetrics({ solution }: { solution: Solution }): React.JSX.Element {
  const m = solution.metrics;
  return (
    <div className="rounded-md border p-3 text-xs">
      <dl className="grid grid-cols-2 gap-x-3 gap-y-2.5">
        <Metric label="Total driving" value={`${m.totalDrivingMinutes.toFixed(0)} min`} />
        <Metric label="Longest single drive" value={`${m.maxDrivingMinutes.toFixed(0)} min`} />
        <Metric label="Total stops" value={m.totalStops.toString()} />
        <Metric label="Longest walk" value={`${m.maxWalkMetres.toFixed(0)} m`} />
        <Metric label="Total walking" value={`${m.totalWalkMetres.toFixed(0)} m`} />
        <FairnessIndicator value={m.fairnessIndex} />
      </dl>
      <div className="mt-3 flex flex-wrap gap-1.5 border-t pt-2">
        {solution.routes.map((r) => (
          <Badge
            key={r.driverParticipantId}
            variant="outline"
            style={{ borderColor: r.colour, color: r.colour }}
          >
            {r.driverDisplayName} · {r.drivingMinutes.toFixed(0)}m
          </Badge>
        ))}
      </div>
    </div>
  );
}

function Metric({ label, value }: { label: string; value: string }): React.JSX.Element {
  return (
    <div>
      <dt className="text-muted-foreground text-[10px] uppercase tracking-wider">{label}</dt>
      <dd className="font-medium tabular-nums">{value}</dd>
    </div>
  );
}

function FairnessIndicator({ value }: { value: number }): React.JSX.Element {
  const bucket = value >= 0.8 ? "good" : value >= 0.6 ? "ok" : "poor";
  const label = bucket === "good" ? "Even" : bucket === "ok" ? "OK" : "Uneven";
  const segments = [
    {
      lit: true,
      colour:
        bucket === "poor" ? "bg-destructive" : bucket === "ok" ? "bg-amber-500" : "bg-success",
    },
    { lit: bucket !== "poor", colour: bucket === "ok" ? "bg-amber-500" : "bg-success" },
    { lit: bucket === "good", colour: "bg-success" },
  ];
  return (
    <div>
      <dt className="text-muted-foreground text-[10px] uppercase tracking-wider">Driver fairness</dt>
      <dd className="mt-0.5 flex items-center gap-1.5">
        <div className="flex gap-0.5">
          {segments.map((seg, i) => (
            <span
              key={i}
              className={
                seg.lit ? seg.colour + " h-2 w-3 rounded-sm" : "h-2 w-3 rounded-sm bg-muted"
              }
            />
          ))}
        </div>
        <span className="font-medium">{label}</span>
      </dd>
    </div>
  );
}
