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
  // Map known tabs onto solutions; if a label is missing we keep the tab disabled.
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
      <h2 className="text-sm font-semibold">Solutions</h2>
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
              <p className="text-muted-foreground text-xs">No solution returned for this tab.</p>
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
                    ? "Locked"
                    : isLocking
                      ? "Locking…"
                      : "Lock this solution"}
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
                    What if…
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
    <dl className="grid grid-cols-2 gap-2 rounded-md border p-3 text-xs">
      <Metric label="Total driving" value={`${m.totalDrivingMinutes.toFixed(0)} min`} />
      <Metric label="Max leg" value={`${m.maxDrivingMinutes.toFixed(0)} min`} />
      <Metric label="Total stops" value={m.totalStops.toString()} />
      <Metric label="Max walk" value={`${m.maxWalkMetres.toFixed(0)} m`} />
      <Metric label="Total walk" value={`${m.totalWalkMetres.toFixed(0)} m`} />
      <Metric label="Fairness" value={m.fairnessIndex.toFixed(2)} />
      <div className="col-span-2 flex flex-wrap gap-1.5 pt-1">
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
    </dl>
  );
}

function Metric({ label, value }: { label: string; value: string }): React.JSX.Element {
  return (
    <div>
      <dt className="text-muted-foreground uppercase tracking-wider">{label}</dt>
      <dd className="font-medium tabular-nums">{value}</dd>
    </div>
  );
}
