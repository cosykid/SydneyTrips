"use client";

import Link from "next/link";
import dynamic from "next/dynamic";
import { useEffect, useRef, useState } from "react";
import { ArrowLeft, Loader2, Route } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Card } from "@/components/ui/card";
import { Separator } from "@/components/ui/separator";
import { useTrip, useOptimise, useRun, useLockSolution } from "@/lib/api/hooks";
import { usePlanStore } from "@/lib/store";
import { WeightSliders } from "./WeightSliders";
import { SolutionPanel } from "./SolutionPanel";
import type { PlanMapProps } from "./PlanMap";
import { WhatIfDialog } from "@/components/whatif/WhatIfDialog";
import { TransitBadge } from "@/components/map/TransitBadge";
import type { Solution } from "@/lib/api/schema";

const PlanMap = dynamic<PlanMapProps>(
  () => import("./PlanMap").then((m) => m.PlanMap),
  {
    ssr: false,
    loading: () => (
      <div className="bg-muted/40 flex h-full w-full items-center justify-center">
        <Loader2 className="text-muted-foreground h-6 w-6 animate-spin" />
      </div>
    ),
  },
);

export interface PlanCanvasProps {
  tripId: string;
}

const RE_OPTIMISE_DEBOUNCE_MS = 1500;

export function PlanCanvas({ tripId }: PlanCanvasProps): React.JSX.Element {
  const trip = useTrip(tripId);
  const optimise = useOptimise();
  const lock = useLockSolution();
  const {
    weights,
    setWeight,
    resetWeights,
    viewState,
    setViewState,
  } = usePlanStore();
  const activeRunId = usePlanStore((s) => s.byTrip[tripId]?.runId);
  const setActiveRunId = usePlanStore((s) => s.setActiveRunId);
  const run = useRun({ tripId, runId: activeRunId, trip: trip.data ?? undefined });

  const [hasOptimisedOnce, setHasOptimisedOnce] = useState(false);
  const [whatIfOpen, setWhatIfOpen] = useState(false);
  const [whatIfSolution, setWhatIfSolution] = useState<Solution | null>(null);
  const debounceTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

  const data = trip.data;

  useEffect(() => {
    if (!hasOptimisedOnce || !data) return;
    if (debounceTimer.current) clearTimeout(debounceTimer.current);
    debounceTimer.current = setTimeout(() => {
      runOptimise(weights);
    }, RE_OPTIMISE_DEBOUNCE_MS);
    return () => {
      if (debounceTimer.current) clearTimeout(debounceTimer.current);
    };
    // eslint-disable-next-line react-hooks/exhaustive-deps
  }, [weights.driverBias, weights.stops, weights.fairness]);

  async function runOptimise(currentWeights = weights) {
    try {
      const { runId } = await optimise.mutateAsync({
        tripId,
        body: { weights: currentWeights },
      });
      setActiveRunId(tripId, runId);
      setHasOptimisedOnce(true);
    } catch (err) {
      toast.error("Could not start planning", {
        description: err instanceof Error ? err.message : undefined,
      });
    }
  }

  async function onLock() {
    const runId = run.data?.id;
    if (!runId) return;
    try {
      // One solution per run, so it's always the first (and only) entry.
      await lock.mutateAsync({ tripId, body: { runId, paretoIndex: 0 } });
      toast.success("Plan saved");
    } catch (err) {
      toast.error("Could not save plan", {
        description: err instanceof Error ? err.message : undefined,
      });
    }
  }

  // The selected solution is now simply whatever the current run produced — sliders re-trigger
  // the run, the run yields a fresh solution, the map redraws. No Pareto carousel layer.
  const selectedSolution = run.data?.solution;
  const hasSolution = Boolean(selectedSolution);

  const computing =
    optimise.isPending ||
    run.data?.status === "pending" ||
    run.data?.status === "running" ||
    (Boolean(activeRunId) &&
      !hasSolution &&
      run.data?.status !== "failed" &&
      run.data?.status !== "cancelled");

  if (trip.isLoading || !data) {
    return (
      <div className="flex h-full items-center justify-center">
        <Loader2 className="text-muted-foreground h-6 w-6 animate-spin" />
      </div>
    );
  }

  const participantCount = data.participants.length;
  const pickupCount = selectedSolution
    ? selectedSolution.metrics.totalStops
    : data.candidateNodes.length;

  return (
    <div className="relative h-full w-full overflow-hidden">
      <div className="absolute inset-0">
        <PlanMap
          destination={{ address: data.destinationAddress, point: data.destination }}
          participants={data.participants}
          candidateNodes={data.candidateNodes}
          solution={selectedSolution}
          trip={{ id: data.id, name: data.name, arriveBy: data.arriveBy }}
          viewState={viewState}
          onMove={setViewState}
        />
      </div>

      <Card
        variant="floating"
        className="absolute top-4 left-4 z-10 flex max-h-[calc(100vh-5.5rem)] w-[360px] flex-col overflow-y-auto p-5"
      >
        <header className="space-y-1.5">
          <Link
            href={`/trips/${tripId}`}
            className="text-muted-foreground hover:text-foreground inline-flex items-center gap-1 text-xs"
          >
            <ArrowLeft className="h-3 w-3" /> Back to trip
          </Link>
          <h1 className="text-lg font-semibold tracking-tight">{data.name}</h1>
          <p className="text-muted-foreground text-xs">
            {participantCount} {participantCount === 1 ? "person" : "people"} ·{" "}
            {pickupCount} pickup {pickupCount === 1 ? "point" : "points"}
          </p>
        </header>

        <Button
          type="button"
          className="mt-4 w-full"
          onClick={() => runOptimise()}
          disabled={computing || optimise.isPending}
        >
          <Route className="mr-2 h-4 w-4" />
          {hasOptimisedOnce ? "Re-plan" : "Plan trip"}
        </Button>

        <Separator className="my-4" />

        <WeightSliders
          weights={weights}
          onChange={setWeight}
          onReset={resetWeights}
          disabled={computing}
        />

        {selectedSolution ? (
          <>
            <Separator className="my-4" />
            <SolutionPanel
              solution={selectedSolution}
              onLock={onLock}
              onWhatIf={(s) => {
                setWhatIfSolution(s);
                setWhatIfOpen(true);
              }}
              isLocking={lock.isPending}
              lockedSolutionId={data.lockedSolutionId}
            />
          </>
        ) : null}

        {run.data?.status === "failed" ? (
          <p className="text-destructive mt-4 text-xs" role="alert">
            Planning failed: {run.data.error ?? "unknown error"}
          </p>
        ) : null}
      </Card>

      <Card
        variant="floating"
        size="sm"
        className="absolute bottom-4 left-1/2 z-10 flex -translate-x-1/2 flex-row flex-wrap items-center justify-center gap-x-3 gap-y-1 px-3 py-2 text-[11px]"
      >
        <LegendDot className="bg-success" /> Pickup
        <LegendDot className="bg-primary" /> People
        <LegendLine /> Car
        <LegendDash color="#475569" /> Walk
        <span className="inline-flex items-center gap-0.5" aria-hidden>
          <TransitBadge modality="train_station" size={12} />
          <TransitBadge modality="bus_stop" size={12} />
          <TransitBadge modality="ferry_wharf" size={12} />
          <TransitBadge modality="light_rail" size={12} />
        </span>{" "}
        Public transport
        <span className="text-[#EA4335]">📍</span> Destination
      </Card>

      {computing ? (
        <div
          className="absolute inset-0 z-20 flex items-center justify-center"
          data-testid="computing-overlay"
        >
          <div className="bg-background/40 absolute inset-0 backdrop-blur-[2px]" />
          <Card variant="floating" className="relative flex flex-col items-center gap-2 px-6 py-4">
            <Loader2 className="text-primary h-5 w-5 animate-spin" />
            <p className="text-sm font-medium">Finding routes…</p>
          </Card>
        </div>
      ) : null}

      {whatIfSolution ? (
        <WhatIfDialog
          open={whatIfOpen}
          onOpenChange={(o) => {
            setWhatIfOpen(o);
            if (!o) setWhatIfSolution(null);
          }}
          tripId={tripId}
          participants={data.participants}
          originalSolution={whatIfSolution}
        />
      ) : null}
    </div>
  );
}

function LegendDot({ className }: { className: string }): React.JSX.Element {
  return <span className={`inline-block h-2 w-2 rounded-full ${className}`} />;
}

function LegendLine(): React.JSX.Element {
  return (
    <span
      aria-hidden
      className="inline-block h-[3px] w-4 rounded-full bg-slate-600"
    />
  );
}

function LegendDash({ color }: { color: string }): React.JSX.Element {
  // Inline SVG so the dash pattern matches the map's GooglePolyline dashes. The colour is
  // explicit because the legend now shows two different dashed strokes (walk vs PT).
  return (
    <svg
      aria-hidden
      width="18"
      height="3"
      viewBox="0 0 18 3"
      className="inline-block"
    >
      <line
        x1="0"
        y1="1.5"
        x2="18"
        y2="1.5"
        stroke={color}
        strokeWidth="2"
        strokeDasharray="3 3"
      />
    </svg>
  );
}
