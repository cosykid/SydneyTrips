"use client";

import { Tabs, TabsList, TabsTrigger, TabsContent } from "@/components/ui/tabs";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import type { Solution } from "@/lib/api/schema";

export interface ParetoCarouselProps {
  solutions: Solution[];
  selectedSolutionId?: string;
  onSelect: (id: string) => void;
  onLock: (id: string) => void;
  onWhatIf?: (solution: Solution) => void;
  isLocking?: boolean;
  lockedSolutionId?: string;
}

const TAB_ORDER: Array<{ value: string; label: string }> = [
  { value: "fastest", label: "Fastest" },
  { value: "fewest_stops", label: "Fewest stops" },
  { value: "least_walking", label: "Least walking" },
];

function findByLabel(solutions: Solution[], label: string): Solution | undefined {
  return solutions.find((s) => s.label === label);
}

export function ParetoCarousel({
  solutions,
  selectedSolutionId,
  onSelect,
  onLock,
  onWhatIf,
  isLocking,
  lockedSolutionId,
}: ParetoCarouselProps): React.JSX.Element {
  const mapping = TAB_ORDER.map((tab) => ({
    ...tab,
    solution:
      findByLabel(solutions, tab.value) ??
      solutions.find((s) => s.label.toLowerCase().includes(tab.label.toLowerCase())),
  }));

  const activeTab =
    mapping.find((t) => t.solution?.id === selectedSolutionId)?.value ??
    mapping.find((t) => t.solution)?.value ??
    TAB_ORDER[0].value;

  return (
    <section className="space-y-3" data-testid="pareto-carousel">
      <h2 className="text-sm font-semibold">Plans</h2>
      <Tabs
        value={activeTab}
        onValueChange={(value) => {
          const sol = mapping.find((t) => t.value === value)?.solution;
          if (sol) onSelect(sol.id);
        }}
      >
        <TabsList className="grid w-full grid-cols-3">
          {mapping.map((t) => (
            <TabsTrigger key={t.value} value={t.value} disabled={!t.solution}>
              {t.label}
            </TabsTrigger>
          ))}
        </TabsList>
        {mapping.map((t) => (
          <TabsContent key={t.value} value={t.value} className="space-y-3 pt-3">
            {t.solution ? (
              <SolutionMetrics solution={t.solution} />
            ) : (
              <p className="text-muted-foreground text-xs">No plan returned for this option.</p>
            )}
            {t.solution ? (
              <div className="flex flex-col gap-1.5">
                <Button
                  type="button"
                  className="w-full"
                  onClick={() => onLock(t.solution!.id)}
                  disabled={isLocking || lockedSolutionId === t.solution.id}
                >
                  {lockedSolutionId === t.solution.id
                    ? "In use"
                    : isLocking
                      ? "Saving…"
                      : "Use this plan"}
                </Button>
                {onWhatIf && lockedSolutionId === t.solution.id ? (
                  <Button
                    type="button"
                    variant="outline"
                    size="sm"
                    className="w-full"
                    onClick={() => onWhatIf(t.solution!)}
                    data-testid="what-if-button"
                  >
                    Try changes
                  </Button>
                ) : null}
              </div>
            ) : null}
          </TabsContent>
        ))}
      </Tabs>
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
    { lit: true, colour: bucket === "poor" ? "bg-destructive" : bucket === "ok" ? "bg-amber-500" : "bg-success" },
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
