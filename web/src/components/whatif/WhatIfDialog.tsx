"use client";

// What-if mode dialog. Lets the user drop participants, add new ones (by
// address — the API will geocode + generate candidate nodes), and override
// the objective weights, then triggers `/trips/{id}/whatif` and shows a
// side-by-side diff against the original.

import { useCallback, useMemo, useState } from "react";
import { Loader2, MinusCircle, PlusCircle, Sparkles } from "lucide-react";
import { toast } from "sonner";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import {
  Dialog,
  DialogClose,
  DialogContent,
  DialogDescription,
  DialogFooter,
  DialogHeader,
  DialogTitle,
} from "@/components/ui/dialog";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Separator } from "@/components/ui/separator";
import { WeightSliders } from "@/components/plan/WeightSliders";
import { useLockSolution, useRun, useWhatIf } from "@/lib/api/hooks";
import type { ObjectiveWeights, Participant, Solution } from "@/lib/api/schema";
import { DEFAULT_WEIGHTS } from "@/lib/api/schema";
import {
  buildWhatIfRequest,
  diffMetrics,
  diffSolutionStops,
  emptyDraft,
  isDraftEmpty,
  type WhatIfDraft,
} from "@/lib/whatif/delta";

export interface WhatIfDialogProps {
  open: boolean;
  onOpenChange: (open: boolean) => void;
  tripId: string;
  participants: Participant[];
  originalSolution: Solution;
}

/**
 * Outer wrapper — uses `key` to fully unmount/remount the inner dialog when
 * it closes so we never have to setState-in-effect to reset draft fields.
 */
export function WhatIfDialog(props: WhatIfDialogProps): React.JSX.Element {
  // Bumping `key` whenever the dialog transitions from open→closed forces a
  // fresh instance of `WhatIfDialogBody`, which is the cheapest, lint-clean
  // way to reset all the draft state.
  const [resetKey, setResetKey] = useState(0);
  const handleOpenChange = useCallback(
    (next: boolean) => {
      if (!next) setResetKey((k) => k + 1);
      props.onOpenChange(next);
    },
    [props],
  );
  return <WhatIfDialogBody key={resetKey} {...props} onOpenChange={handleOpenChange} />;
}

function WhatIfDialogBody({
  open,
  onOpenChange,
  tripId,
  participants,
  originalSolution,
}: WhatIfDialogProps): React.JSX.Element {
  const [draft, setDraft] = useState<WhatIfDraft>(() => emptyDraft());
  const [weights, setWeights] = useState<ObjectiveWeights>({ ...DEFAULT_WEIGHTS });
  const [overrideWeights, setOverrideWeights] = useState(false);
  const [addEntry, setAddEntry] = useState({
    displayName: "",
    originAddress: "",
    role: "passenger" as Participant["role"],
  });
  const [activeRunId, setActiveRunId] = useState<string | undefined>(undefined);
  const [showCompare, setShowCompare] = useState(false);

  const whatIfMut = useWhatIf();
  const lockMut = useLockSolution();
  const run = useRun({ tripId, runId: activeRunId });

  const newSolution: Solution | undefined = run.data?.solution;
  const stopDiff = useMemo(
    () => (newSolution ? diffSolutionStops(originalSolution, newSolution) : []),
    [originalSolution, newSolution],
  );
  const metricsDiff = useMemo(
    () => (newSolution ? diffMetrics(originalSolution, newSolution) : null),
    [originalSolution, newSolution],
  );

  const onToggleDrop = useCallback((id: string) => {
    setDraft((d) => {
      const next = new Set(d.dropParticipantIds);
      if (next.has(id)) next.delete(id);
      else next.add(id);
      return { ...d, dropParticipantIds: next };
    });
  }, []);

  const onAddCandidate = useCallback(() => {
    if (!addEntry.displayName.trim() || !addEntry.originAddress.trim()) return;
    setDraft((d) => ({
      ...d,
      addParticipants: [
        ...d.addParticipants,
        {
          displayName: addEntry.displayName.trim(),
          originAddress: addEntry.originAddress.trim(),
          role: addEntry.role,
        },
      ],
    }));
    setAddEntry({ displayName: "", originAddress: "", role: "passenger" });
  }, [addEntry]);

  const onRemoveAddCandidate = useCallback((idx: number) => {
    setDraft((d) => ({
      ...d,
      addParticipants: d.addParticipants.filter((_, i) => i !== idx),
    }));
  }, []);

  async function onReoptimise(): Promise<void> {
    const draftWithWeights: WhatIfDraft = {
      ...draft,
      newWeights: overrideWeights ? weights : undefined,
    };
    if (isDraftEmpty(draftWithWeights)) {
      toast.error("Add at least one change before re-optimising");
      return;
    }
    try {
      const body = buildWhatIfRequest(draftWithWeights);
      const { runId } = await whatIfMut.mutateAsync({ tripId, body });
      setActiveRunId(runId);
      setShowCompare(true);
      toast.info("Re-optimising trip…");
    } catch (err) {
      toast.error("Could not start what-if", {
        description: err instanceof Error ? err.message : undefined,
      });
    }
  }

  async function onAccept(): Promise<void> {
    if (!newSolution) return;
    try {
      await lockMut.mutateAsync({ tripId, body: { solutionId: newSolution.id } });
      toast.success("What-if accepted — solution locked");
      onOpenChange(false);
    } catch (err) {
      toast.error("Could not lock what-if solution", {
        description: err instanceof Error ? err.message : undefined,
      });
    }
  }

  function onDiscard(): void {
    setActiveRunId(undefined);
    setShowCompare(false);
  }

  const draftIsEmpty = useMemo(
    () => isDraftEmpty({ ...draft, newWeights: overrideWeights ? weights : undefined }),
    [draft, overrideWeights, weights],
  );

  const computing =
    whatIfMut.isPending ||
    (activeRunId &&
      run.data?.status !== "completed" &&
      run.data?.status !== "failed");

  return (
    <Dialog open={open} onOpenChange={onOpenChange}>
      <DialogContent className="!max-w-2xl">
        <DialogHeader>
          <DialogTitle>What if…</DialogTitle>
          <DialogDescription>
            Tweak who&apos;s on the trip and re-solve while keeping the rest of the schedule
            intact.
          </DialogDescription>
        </DialogHeader>

        {showCompare && newSolution ? (
          <CompareView
            stopDiff={stopDiff}
            metricsDiff={metricsDiff}
            originalSolution={originalSolution}
            newSolution={newSolution}
            onAccept={onAccept}
            onDiscard={onDiscard}
            isLocking={lockMut.isPending}
          />
        ) : showCompare && computing ? (
          <div className="text-muted-foreground flex flex-col items-center gap-2 py-10 text-sm">
            <Loader2 className="h-5 w-5 animate-spin" /> Re-optimising…
          </div>
        ) : showCompare && run.data?.status === "failed" ? (
          <div className="text-destructive py-6 text-sm" role="alert">
            What-if failed: {run.data.error ?? "unknown error"}
            <Button
              type="button"
              variant="outline"
              size="sm"
              className="ml-2"
              onClick={() => setShowCompare(false)}
            >
              Back to edit
            </Button>
          </div>
        ) : (
          <div className="max-h-[60vh] space-y-5 overflow-y-auto pr-1">
            <section className="space-y-2">
              <h3 className="text-sm font-semibold">Drop participants</h3>
              <ul className="grid grid-cols-1 gap-1.5 sm:grid-cols-2">
                {participants.map((p) => {
                  const isDropped = draft.dropParticipantIds.has(p.id);
                  return (
                    <li key={p.id}>
                      <label className="hover:bg-muted/50 flex items-center justify-between gap-2 rounded-md border px-3 py-2 text-sm">
                        <span className="flex items-center gap-2">
                          <input
                            type="checkbox"
                            className="accent-primary"
                            checked={isDropped}
                            onChange={() => onToggleDrop(p.id)}
                            data-testid={`drop-${p.id}`}
                          />
                          <span className={isDropped ? "line-through" : ""}>{p.displayName}</span>
                          {p.role === "driver" ? (
                            <Badge variant="outline">Driver</Badge>
                          ) : null}
                        </span>
                        <span className="text-muted-foreground text-xs">
                          {p.originAddress.slice(0, 40)}
                          {p.originAddress.length > 40 ? "…" : ""}
                        </span>
                      </label>
                    </li>
                  );
                })}
              </ul>
            </section>

            <Separator />

            <section className="space-y-2">
              <h3 className="text-sm font-semibold">Add participants</h3>
              <div className="grid grid-cols-1 gap-2 sm:grid-cols-[1fr_1.5fr_auto]">
                <div className="space-y-1">
                  <Label htmlFor="add-name">Name</Label>
                  <Input
                    id="add-name"
                    value={addEntry.displayName}
                    onChange={(e) =>
                      setAddEntry((p) => ({ ...p, displayName: e.target.value }))
                    }
                  />
                </div>
                <div className="space-y-1">
                  <Label htmlFor="add-address">Address</Label>
                  <Input
                    id="add-address"
                    value={addEntry.originAddress}
                    onChange={(e) =>
                      setAddEntry((p) => ({ ...p, originAddress: e.target.value }))
                    }
                    placeholder="123 Glebe Pt Rd, Glebe"
                  />
                </div>
                <div className="flex items-end">
                  <Button
                    type="button"
                    size="sm"
                    onClick={onAddCandidate}
                    disabled={!addEntry.displayName.trim() || !addEntry.originAddress.trim()}
                  >
                    <PlusCircle className="mr-1 h-3.5 w-3.5" />
                    Add
                  </Button>
                </div>
              </div>
              {draft.addParticipants.length > 0 ? (
                <ul className="divide-y rounded-md border text-xs">
                  {draft.addParticipants.map((p, idx) => (
                    <li
                      key={`${p.displayName}-${idx}`}
                      className="flex items-center justify-between p-2"
                    >
                      <div>
                        <span className="font-medium">{p.displayName}</span>
                        <span className="text-muted-foreground"> · {p.originAddress}</span>
                      </div>
                      <Button
                        type="button"
                        variant="ghost"
                        size="xs"
                        onClick={() => onRemoveAddCandidate(idx)}
                        aria-label={`Remove ${p.displayName}`}
                      >
                        <MinusCircle className="h-3.5 w-3.5" />
                      </Button>
                    </li>
                  ))}
                </ul>
              ) : null}
            </section>

            <Separator />

            <section className="space-y-2">
              <div className="flex items-center justify-between">
                <h3 className="text-sm font-semibold">Override weights</h3>
                <label className="text-muted-foreground flex items-center gap-1.5 text-xs">
                  <input
                    type="checkbox"
                    className="accent-primary"
                    checked={overrideWeights}
                    onChange={(e) => setOverrideWeights(e.target.checked)}
                  />
                  Use new weights
                </label>
              </div>
              {overrideWeights ? (
                <WeightSliders
                  weights={weights}
                  onChange={(key, value) =>
                    setWeights((w) => ({ ...w, [key]: value }))
                  }
                  onReset={() => setWeights({ ...DEFAULT_WEIGHTS })}
                />
              ) : (
                <p className="text-muted-foreground text-xs">
                  Tick to override the objective weights for this what-if run.
                </p>
              )}
            </section>

            <Separator />

            <section className="space-y-2">
              <label className="text-muted-foreground flex items-center gap-1.5 text-xs">
                <input
                  type="checkbox"
                  className="accent-primary"
                  checked={draft.repair}
                  onChange={(e) => setDraft((d) => ({ ...d, repair: e.target.checked }))}
                />
                Warm-start from locked solution (minimise churn for unaffected riders)
              </label>
            </section>
          </div>
        )}

        <DialogFooter>
          {!showCompare ? (
            <>
              <DialogClose render={<Button variant="outline" />}>Cancel</DialogClose>
              <Button
                type="button"
                onClick={onReoptimise}
                disabled={draftIsEmpty || Boolean(computing)}
                data-testid="re-optimise"
              >
                {computing ? (
                  <Loader2 className="mr-1 h-3.5 w-3.5 animate-spin" />
                ) : (
                  <Sparkles className="mr-1 h-3.5 w-3.5" />
                )}
                Re-optimise
              </Button>
            </>
          ) : null}
        </DialogFooter>
      </DialogContent>
    </Dialog>
  );
}

interface CompareViewProps {
  stopDiff: ReturnType<typeof diffSolutionStops>;
  metricsDiff: ReturnType<typeof diffMetrics> | null;
  originalSolution: Solution;
  newSolution: Solution;
  onAccept: () => void;
  onDiscard: () => void;
  isLocking: boolean;
}

function CompareView({
  stopDiff,
  metricsDiff,
  originalSolution,
  newSolution,
  onAccept,
  onDiscard,
  isLocking,
}: CompareViewProps): React.JSX.Element {
  const kept = stopDiff.filter((s) => s.state === "kept").length;
  const added = stopDiff.filter((s) => s.state === "added").length;
  const removed = stopDiff.filter((s) => s.state === "removed").length;
  return (
    <div className="space-y-4">
      <div className="grid grid-cols-3 gap-3 text-center text-xs" data-testid="diff-counts">
        <Card label="Kept" value={kept} variant="muted" />
        <Card label="Added" value={added} variant="add" />
        <Card label="Removed" value={removed} variant="remove" />
      </div>
      {metricsDiff ? (
        <div className="grid grid-cols-2 gap-3 rounded-md border p-3 text-xs sm:grid-cols-4">
          <Metric
            label="Driving min"
            before={originalSolution.metrics.totalDrivingMinutes.toFixed(0)}
            after={newSolution.metrics.totalDrivingMinutes.toFixed(0)}
            delta={metricsDiff.drivingMinutesDelta}
            unit=" min"
          />
          <Metric
            label="Stops"
            before={originalSolution.metrics.totalStops.toString()}
            after={newSolution.metrics.totalStops.toString()}
            delta={metricsDiff.stopsDelta}
          />
          <Metric
            label="Walk (m)"
            before={originalSolution.metrics.totalWalkMetres.toFixed(0)}
            after={newSolution.metrics.totalWalkMetres.toFixed(0)}
            delta={metricsDiff.totalWalkMetresDelta}
            unit=" m"
          />
          <Metric
            label="Fairness"
            before={originalSolution.metrics.fairnessIndex.toFixed(2)}
            after={newSolution.metrics.fairnessIndex.toFixed(2)}
            delta={metricsDiff.fairnessDelta}
            higherIsBetter
          />
        </div>
      ) : null}
      <ul className="max-h-48 space-y-1 overflow-y-auto rounded-md border p-2 text-xs">
        {stopDiff.map((entry) => (
          <li
            key={`${entry.state}-${entry.key}`}
            className={
              entry.state === "added"
                ? "text-emerald-600 dark:text-emerald-400"
                : entry.state === "removed"
                  ? "text-destructive line-through"
                  : "text-muted-foreground"
            }
          >
            {entry.state === "added" ? "+ " : entry.state === "removed" ? "− " : "  "}
            {entry.lat.toFixed(4)}, {entry.lng.toFixed(4)} — {entry.passengerIds.length} pickup
            {entry.passengerIds.length === 1 ? "" : "s"}
          </li>
        ))}
      </ul>
      <DialogFooter>
        <Button type="button" variant="outline" onClick={onDiscard}>
          Discard
        </Button>
        <Button type="button" onClick={onAccept} disabled={isLocking} data-testid="accept-whatif">
          {isLocking ? "Locking…" : "Accept what-if"}
        </Button>
      </DialogFooter>
    </div>
  );
}

function Card({
  label,
  value,
  variant,
}: {
  label: string;
  value: number;
  variant: "muted" | "add" | "remove";
}): React.JSX.Element {
  const colours: Record<typeof variant, string> = {
    muted: "bg-muted text-muted-foreground",
    add: "bg-emerald-100 text-emerald-700 dark:bg-emerald-900/30 dark:text-emerald-300",
    remove: "bg-destructive/10 text-destructive",
  };
  return (
    <div className={`flex flex-col rounded-md px-3 py-2 ${colours[variant]}`}>
      <span className="text-[10px] uppercase tracking-wider">{label}</span>
      <span className="text-lg font-semibold tabular-nums">{value}</span>
    </div>
  );
}

function Metric({
  label,
  before,
  after,
  delta,
  unit = "",
  higherIsBetter = false,
}: {
  label: string;
  before: string;
  after: string;
  delta: number;
  unit?: string;
  higherIsBetter?: boolean;
}): React.JSX.Element {
  const goodDirection = higherIsBetter ? delta >= 0 : delta <= 0;
  return (
    <div className="space-y-0.5">
      <dt className="text-muted-foreground uppercase tracking-wider">{label}</dt>
      <dd className="font-medium tabular-nums">
        {before}
        {unit} → {after}
        {unit}
      </dd>
      <dd
        className={`text-[10px] font-medium ${goodDirection ? "text-emerald-600 dark:text-emerald-400" : "text-amber-600 dark:text-amber-400"}`}
      >
        {delta > 0 ? "+" : ""}
        {delta.toFixed(unit === "" ? 2 : 1)}
        {unit}
      </dd>
    </div>
  );
}
