"use client";

import Link from "next/link";
import dynamic from "next/dynamic";
import { useEffect, useMemo, useRef, useState } from "react";
import { ArrowLeft, Loader2, Sparkles } from "lucide-react";
import { toast } from "sonner";
import { Button } from "@/components/ui/button";
import { Separator } from "@/components/ui/separator";
import {
  useTrip,
  useOptimise,
  useRun,
  usePareto,
  useLockSolution,
} from "@/lib/api/hooks";
import { usePlanStore } from "@/lib/store";
import { WeightSliders } from "./WeightSliders";
import { ParetoCarousel } from "./ParetoCarousel";
import type { PlanMapProps } from "./PlanMap";

// Mapbox-gl reaches for `window` at module load, so render the map only on
// the client. Loading skeleton keeps SSR happy.
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
    activeRunId,
    setActiveRunId,
    selectedSolutionId,
    selectSolution,
    viewState,
    setViewState,
  } = usePlanStore();
  const run = useRun({ tripId, runId: activeRunId });
  const pareto = usePareto(tripId, activeRunId);

  const [hasOptimisedOnce, setHasOptimisedOnce] = useState(false);
  const debounceTimer = useRef<ReturnType<typeof setTimeout> | null>(null);

  const data = trip.data;

  // Re-optimise on slider change once the user has done their first run.
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
  }, [weights.drivingTime, weights.stops, weights.walking, weights.fairness]);

  // Auto-select first pareto solution once available.
  useEffect(() => {
    if (pareto.data?.solutions.length && !selectedSolutionId) {
      selectSolution(pareto.data.solutions[0].id);
    }
  }, [pareto.data, selectedSolutionId, selectSolution]);

  async function runOptimise(currentWeights = weights) {
    try {
      const { runId } = await optimise.mutateAsync({
        tripId,
        body: { weights: currentWeights },
      });
      setActiveRunId(runId);
      selectSolution(undefined);
      setHasOptimisedOnce(true);
    } catch (err) {
      toast.error("Could not start optimisation", {
        description: err instanceof Error ? err.message : undefined,
      });
    }
  }

  async function onLock(solutionId: string) {
    try {
      await lock.mutateAsync({ tripId, body: { solutionId } });
      toast.success("Solution locked");
    } catch (err) {
      toast.error("Could not lock", {
        description: err instanceof Error ? err.message : undefined,
      });
    }
  }

  const computing =
    optimise.isPending ||
    run.data?.status === "queued" ||
    run.data?.status === "running" ||
    (Boolean(activeRunId) && !pareto.data && run.data?.status !== "failed");

  const selectedSolution = useMemo(
    () => pareto.data?.solutions.find((s) => s.id === selectedSolutionId),
    [pareto.data, selectedSolutionId],
  );

  if (trip.isLoading || !data) {
    return (
      <div className="flex h-full items-center justify-center">
        <Loader2 className="text-muted-foreground h-6 w-6 animate-spin" />
      </div>
    );
  }

  return (
    <div className="flex h-full w-full">
      <div className="relative flex-1">
        <PlanMap
          destination={{ address: data.destinationAddress, point: data.destination }}
          participants={data.participants}
          candidateNodes={data.candidateNodes}
          solution={selectedSolution}
          trip={{ id: data.id, name: data.name }}
          viewState={viewState}
          onMove={setViewState}
        />
        {computing ? (
          <div
            className="bg-background/85 absolute inset-0 flex items-center justify-center backdrop-blur-sm"
            data-testid="computing-overlay"
          >
            <div className="bg-card flex flex-col items-center gap-2 rounded-md border px-6 py-4 shadow-sm">
              <Loader2 className="text-foreground h-5 w-5 animate-spin" />
              <p className="text-sm font-medium">Computing…</p>
              <p className="text-muted-foreground text-xs">
                {run.data?.status ?? optimise.status}
              </p>
            </div>
          </div>
        ) : null}
        <div className="bg-background/90 absolute left-4 top-4 flex items-center gap-2 rounded-md border px-3 py-1.5 text-xs shadow-sm">
          <Link href={`/trips/${tripId}`} className="flex items-center gap-1.5">
            <ArrowLeft className="h-3.5 w-3.5" /> {data.name}
          </Link>
        </div>
      </div>
      <aside className="bg-card flex h-full w-96 flex-col overflow-y-auto border-l">
        <div className="space-y-5 p-5">
          <header className="space-y-1">
            <h1 className="text-base font-semibold">Planner</h1>
            <p className="text-muted-foreground text-xs">
              {data.participants.length} participant
              {data.participants.length === 1 ? "" : "s"} · {data.candidateNodes.length} candidate
              nodes
            </p>
          </header>
          <Button
            type="button"
            className="w-full"
            onClick={() => runOptimise()}
            disabled={computing || optimise.isPending}
          >
            <Sparkles className="mr-2 h-4 w-4" />
            {hasOptimisedOnce ? "Re-run optimisation" : "Optimise"}
          </Button>
          <Separator />
          <WeightSliders
            weights={weights}
            onChange={setWeight}
            onReset={resetWeights}
            disabled={computing}
          />
          {pareto.data ? (
            <>
              <Separator />
              <ParetoCarousel
                solutions={pareto.data.solutions}
                selectedSolutionId={selectedSolutionId}
                onSelect={selectSolution}
                onLock={onLock}
                isLocking={lock.isPending}
                lockedSolutionId={data.lockedSolutionId}
              />
            </>
          ) : null}
          {run.data?.status === "failed" ? (
            <p className="text-destructive text-xs" role="alert">
              Optimisation failed: {run.data.error ?? "unknown error"}
            </p>
          ) : null}
        </div>
      </aside>
    </div>
  );
}
