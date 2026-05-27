"use client";

import { useMemo, useState } from "react";
import { ChevronRight, ListChecks, Scale, Timer } from "lucide-react";
import clsx from "clsx";
import { Slider } from "@/components/ui/slider";
import { Label } from "@/components/ui/label";
import { Button } from "@/components/ui/button";
import { DEFAULT_WEIGHTS, type ObjectiveWeights } from "@/lib/api/schema";

export interface WeightSlidersProps {
  weights: ObjectiveWeights;
  onChange: (key: keyof ObjectiveWeights, value: number) => void;
  onReset: () => void;
  disabled?: boolean;
}

interface Preset {
  id: "fastest" | "fewest_stops" | "fair_share";
  label: string;
  icon: React.ComponentType<{ className?: string }>;
  weights: ObjectiveWeights;
}

const PRESETS: Preset[] = [
  {
    id: "fastest",
    label: "Less driving",
    icon: Timer,
    weights: { driverBias: 0.75, stops: 0.1, fairness: 0.2 },
  },
  {
    id: "fewest_stops",
    label: "Fewest stops",
    icon: ListChecks,
    weights: { driverBias: 0.5, stops: 0.5, fairness: 0.2 },
  },
  {
    id: "fair_share",
    label: "Fair share",
    icon: Scale,
    weights: { driverBias: 0.5, stops: 0.15, fairness: 0.6 },
  },
];

const ROWS: Array<{ key: keyof ObjectiveWeights; label: string; help: string }> = [
  {
    key: "driverBias",
    label: "Driving vs public transport",
    help: "Choose whether the plan protects passenger PT time or driver time.",
  },
  { key: "stops", label: "Number of stops", help: "Fewer pickup stops along the way." },
  { key: "fairness", label: "Fair sharing", help: "Share pickup detours evenly across drivers." },
];

const EPS = 0.001;

function matchPreset(w: ObjectiveWeights): Preset["id"] | null {
  for (const p of PRESETS) {
    if (
      Math.abs(p.weights.driverBias - w.driverBias) < EPS &&
      Math.abs(p.weights.stops - w.stops) < EPS &&
      Math.abs(p.weights.fairness - w.fairness) < EPS
    ) {
      return p.id;
    }
  }
  return null;
}

export function WeightSliders({
  weights,
  onChange,
  onReset,
  disabled,
}: WeightSlidersProps): React.JSX.Element {
  const activePreset = useMemo(() => matchPreset(weights), [weights]);
  const [advancedExplicit, setAdvancedExplicit] = useState<boolean | null>(null);
  const advancedOpen = advancedExplicit ?? activePreset === null;

  function applyPreset(preset: Preset): void {
    onChange("driverBias", preset.weights.driverBias);
    onChange("stops", preset.weights.stops);
    onChange("fairness", preset.weights.fairness);
  }

  return (
    <section className="space-y-3">
      <div>
        <h2 className="text-sm font-semibold">Route priorities</h2>
        <p className="text-muted-foreground mt-0.5 text-xs">
          Pick what matters most for this trip.
        </p>
      </div>
      <div className="grid grid-cols-3 gap-1.5" role="radiogroup" aria-label="Route priorities">
        {PRESETS.map((preset) => {
          const Icon = preset.icon;
          const active = activePreset === preset.id;
          return (
            <button
              key={preset.id}
              type="button"
              role="radio"
              aria-checked={active}
              disabled={disabled}
              onClick={() => applyPreset(preset)}
              data-testid={`preset-${preset.id}`}
              className={clsx(
                "flex flex-col items-center gap-1 rounded-lg border px-2 py-2.5 text-xs font-medium transition-colors",
                "disabled:cursor-not-allowed disabled:opacity-50",
                active
                  ? "border-primary bg-primary/10 text-primary"
                  : "border-border bg-card text-foreground/80 hover:bg-accent",
              )}
            >
              <Icon className="h-4 w-4" />
              {preset.label}
            </button>
          );
        })}
      </div>
      <details
        open={advancedOpen}
        onToggle={(e) =>
          setAdvancedExplicit((e.currentTarget as HTMLDetailsElement).open)
        }
        className="group/advanced"
      >
        <summary className="text-muted-foreground hover:text-foreground flex cursor-pointer list-none items-center gap-1 text-xs select-none">
          <ChevronRight className="h-3 w-3 transition-transform group-open/advanced:rotate-90" />
          Advanced
        </summary>
        <div className="mt-3 space-y-3.5">
          {ROWS.map((row) => (
            <div key={row.key} className="space-y-1.5" data-testid={`slider-${row.key}`}>
              <div className="flex items-center justify-between">
                <Label htmlFor={`w-${row.key}`} className="text-xs">
                  {row.label}
                </Label>
                <span className="text-muted-foreground text-xs">
                  {formatWeightValue(row.key, weights[row.key])}
                </span>
              </div>
              <Slider
                id={`w-${row.key}`}
                min={0}
                max={1}
                step={0.05}
                value={weights[row.key]}
                onValueChange={(next) =>
                  onChange(row.key, Array.isArray(next) ? (next[0] ?? 0) : next)
                }
                disabled={disabled}
                aria-label={row.label}
              />
              {row.key === "driverBias" ? (
                <div className="text-muted-foreground flex items-center justify-between text-[11px]">
                  <span>Passenger time</span>
                  <span>Driver time</span>
                </div>
              ) : null}
              <p className="text-muted-foreground text-[11px]">{row.help}</p>
            </div>
          ))}
          <Button
            type="button"
            variant="ghost"
            size="sm"
            onClick={() => {
              onReset();
            }}
            disabled={disabled || matchesDefaults(weights)}
            data-testid="reset-weights"
          >
            Reset to defaults
          </Button>
        </div>
      </details>
    </section>
  );
}

function matchesDefaults(w: ObjectiveWeights): boolean {
  return (
    Math.abs(w.driverBias - DEFAULT_WEIGHTS.driverBias) < EPS &&
    Math.abs(w.stops - DEFAULT_WEIGHTS.stops) < EPS &&
    Math.abs(w.fairness - DEFAULT_WEIGHTS.fairness) < EPS
  );
}

function formatWeightValue(key: keyof ObjectiveWeights, value: number): string {
  if (key !== "driverBias") return `${Math.round(value * 100)}%`;
  if (value <= 0.2) return "Passenger time";
  if (value < 0.45) return "Leans passenger";
  if (value <= 0.55) return "Balanced";
  if (value < 0.8) return "Leans driver";
  return "Driver time";
}
