"use client";

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

const ROWS: Array<{ key: keyof ObjectiveWeights; label: string; help: string }> = [
  { key: "drivingTime", label: "Driving time", help: "Penalise total minutes on the road." },
  { key: "stops", label: "Stops", help: "Prefer fewer pickup stops." },
  { key: "walking", label: "Walking", help: "Penalise walking from pickup nodes." },
  { key: "fairness", label: "Fairness", help: "Balance load across drivers." },
];

export function WeightSliders({
  weights,
  onChange,
  onReset,
  disabled,
}: WeightSlidersProps): React.JSX.Element {
  return (
    <section className="space-y-4">
      <div className="flex items-center justify-between">
        <h2 className="text-sm font-semibold">Objective weights</h2>
        <Button
          type="button"
          variant="ghost"
          size="sm"
          onClick={onReset}
          disabled={disabled}
          data-testid="reset-weights"
        >
          Reset
        </Button>
      </div>
      <div className="space-y-4">
        {ROWS.map((row) => (
          <div key={row.key} className="space-y-1.5" data-testid={`slider-${row.key}`}>
            <div className="flex items-center justify-between">
              <Label htmlFor={`w-${row.key}`}>{row.label}</Label>
              <span className="text-muted-foreground tabular-nums text-xs">
                {weights[row.key].toFixed(2)}
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
            <p className="text-muted-foreground text-xs">{row.help}</p>
          </div>
        ))}
      </div>
      <p className="text-muted-foreground text-xs">
        Defaults: {Object.entries(DEFAULT_WEIGHTS).map(([k, v]) => `${k} ${v}`).join(", ")}.
      </p>
    </section>
  );
}
