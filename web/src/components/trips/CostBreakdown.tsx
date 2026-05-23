"use client";

// Cost-split UI for `/trips/[id]/cost`. Replaces the Phase-B placeholder with:
//   - editable fuel-price + fuel-economy inputs (debounced refetch on change)
//   - card-per-participant breakdown (fuel + tolls + total), sortable
//   - "Export CSV" client-side download
//   - summary footer with "driver pays nothing" callout when applicable
//
// The WS7 endpoint hasn't necessarily shipped — we treat any response shape as
// "what we can render" and tolerate zeros + missing breakdowns gracefully.

import { useEffect, useMemo, useState } from "react";
import { Download, Loader2 } from "lucide-react";
import { Badge } from "@/components/ui/badge";
import { Button } from "@/components/ui/button";
import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Input } from "@/components/ui/input";
import { Label } from "@/components/ui/label";
import { Separator } from "@/components/ui/separator";
import { useCostSplit, useTrip } from "@/lib/api/hooks";
import { DEFAULT_COST_INPUTS } from "@/lib/api/schema";
import type { CostSplit } from "@/lib/api/schema";

type SortKey = "name" | "amount" | "fuel" | "tolls";
type SortDir = "asc" | "desc";

interface BreakdownRow {
  participantId: string;
  displayName: string;
  fuel: number;
  tolls: number;
  amount: number;
  /** Driver flag — used for the "pays nothing" callout. */
  isDriver: boolean;
}

function buildRows(
  split: CostSplit | undefined,
  participantNames: Map<string, string>,
  driverIds: Set<string>,
): BreakdownRow[] {
  if (!split?.perParticipant?.length) {
    // Synthesise zero rows so the UI renders one entry per participant.
    return Array.from(participantNames.entries()).map(([id, name]) => ({
      participantId: id,
      displayName: name,
      fuel: 0,
      tolls: 0,
      amount: 0,
      isDriver: driverIds.has(id),
    }));
  }
  return split.perParticipant.map((p) => ({
    participantId: p.participantId,
    displayName: p.displayName || participantNames.get(p.participantId) || "Unknown",
    fuel: p.breakdown?.fuel ?? 0,
    tolls: p.breakdown?.tolls ?? 0,
    amount: p.amount ?? (p.breakdown ? p.breakdown.fuel + p.breakdown.tolls : 0),
    isDriver: driverIds.has(p.participantId),
  }));
}

function sortRows(rows: BreakdownRow[], key: SortKey, dir: SortDir): BreakdownRow[] {
  const mul = dir === "asc" ? 1 : -1;
  return [...rows].sort((a, b) => {
    let cmp = 0;
    if (key === "name") cmp = a.displayName.localeCompare(b.displayName);
    else if (key === "amount") cmp = a.amount - b.amount;
    else if (key === "fuel") cmp = a.fuel - b.fuel;
    else if (key === "tolls") cmp = a.tolls - b.tolls;
    return cmp * mul;
  });
}

function toCsv(rows: BreakdownRow[], currency: string): string {
  const header = ["Participant", "Fuel", "Tolls", "Total", "Currency"];
  const body = rows.map((r) => [
    JSON.stringify(r.displayName),
    r.fuel.toFixed(2),
    r.tolls.toFixed(2),
    r.amount.toFixed(2),
    currency,
  ]);
  return [header.join(","), ...body.map((b) => b.join(","))].join("\n") + "\n";
}

export function CostBreakdown({ tripId }: { tripId: string }): React.JSX.Element {
  const trip = useTrip(tripId);
  const split = useCostSplit(tripId);
  const [inputs, setInputs] = useState(DEFAULT_COST_INPUTS);
  const [sortKey, setSortKey] = useState<SortKey>("amount");
  const [sortDir, setSortDir] = useState<SortDir>("desc");

  // Debounce refetch when the user tweaks the cost inputs. The mock endpoint
  // doesn't yet take these as query params, but the UI is correct in advance
  // — when WS7 wires them in we'll have to thread them through `useCostSplit`.
  useEffect(() => {
    const id = setTimeout(() => {
      split.refetch();
    }, 350);
    return () => clearTimeout(id);
  }, [inputs.fuelPricePerLitre, inputs.litresPer100Km, split]);

  const participantNames = useMemo<Map<string, string>>(() => {
    const map = new Map<string, string>();
    for (const p of trip.data?.participants ?? []) map.set(p.id, p.displayName);
    return map;
  }, [trip.data]);

  const driverIds = useMemo<Set<string>>(() => {
    const set = new Set<string>();
    for (const p of trip.data?.participants ?? []) {
      if (p.role === "driver") set.add(p.id);
    }
    return set;
  }, [trip.data]);

  const rawRows = useMemo<BreakdownRow[]>(
    () => buildRows(split.data, participantNames, driverIds),
    [split.data, participantNames, driverIds],
  );
  const rows = useMemo<BreakdownRow[]>(
    () => sortRows(rawRows, sortKey, sortDir),
    [rawRows, sortKey, sortDir],
  );

  const totals = useMemo(() => {
    const totalFuel = rawRows.reduce((s, r) => s + r.fuel, 0);
    const totalTolls = rawRows.reduce((s, r) => s + r.tolls, 0);
    const total = rawRows.reduce((s, r) => s + r.amount, 0);
    const driversAtZero = rawRows
      .filter((r) => r.isDriver)
      .every((r) => Math.abs(r.amount) < 0.005);
    return { totalFuel, totalTolls, total, driversAtZero };
  }, [rawRows]);

  function toggleSort(key: SortKey): void {
    if (sortKey === key) {
      setSortDir((d) => (d === "asc" ? "desc" : "asc"));
    } else {
      setSortKey(key);
      setSortDir(key === "name" ? "asc" : "desc");
    }
  }

  function downloadCsv(): void {
    const csv = toCsv(rows, split.data?.currency ?? "AUD");
    const blob = new Blob([csv], { type: "text/csv;charset=utf-8" });
    const url = URL.createObjectURL(blob);
    const a = document.createElement("a");
    a.href = url;
    a.download = `trip-${tripId.slice(0, 8)}-cost-split.csv`;
    a.click();
    URL.revokeObjectURL(url);
  }

  if (trip.isLoading) {
    return (
      <div className="text-muted-foreground flex items-center gap-2 text-sm">
        <Loader2 className="h-4 w-4 animate-spin" /> Loading trip…
      </div>
    );
  }

  if (!trip.data?.hasLockedSolution) {
    return (
      <Card className="border-dashed">
        <CardContent className="text-muted-foreground py-10 text-center text-sm">
          Lock a solution from the planner first — cost split needs a chosen route.
        </CardContent>
      </Card>
    );
  }

  return (
    <div className="space-y-5">
      <Card>
        <CardHeader>
          <CardTitle className="text-base">Cost inputs</CardTitle>
        </CardHeader>
        <CardContent className="grid grid-cols-1 gap-4 sm:grid-cols-3">
          <div className="space-y-1.5">
            <Label htmlFor="fuel-price">Fuel price (A$/L)</Label>
            <Input
              id="fuel-price"
              type="number"
              step="0.01"
              min={0}
              value={inputs.fuelPricePerLitre}
              onChange={(e) =>
                setInputs((p) => ({
                  ...p,
                  fuelPricePerLitre: Number(e.target.value) || 0,
                }))
              }
              data-testid="fuel-price-input"
            />
          </div>
          <div className="space-y-1.5">
            <Label htmlFor="fuel-economy">Fuel economy (L/100 km)</Label>
            <Input
              id="fuel-economy"
              type="number"
              step="0.1"
              min={0}
              value={inputs.litresPer100Km}
              onChange={(e) =>
                setInputs((p) => ({
                  ...p,
                  litresPer100Km: Number(e.target.value) || 0,
                }))
              }
              data-testid="fuel-economy-input"
            />
          </div>
          <div className="flex items-end justify-end gap-2">
            <Button
              type="button"
              variant="outline"
              size="sm"
              onClick={() => setInputs(DEFAULT_COST_INPUTS)}
              disabled={
                inputs.fuelPricePerLitre === DEFAULT_COST_INPUTS.fuelPricePerLitre &&
                inputs.litresPer100Km === DEFAULT_COST_INPUTS.litresPer100Km
              }
            >
              Reset
            </Button>
            <Button
              type="button"
              size="sm"
              onClick={downloadCsv}
              data-testid="export-csv"
              disabled={rows.length === 0}
            >
              <Download className="mr-1 h-3.5 w-3.5" />
              Export CSV
            </Button>
          </div>
        </CardContent>
      </Card>

      <Card>
        <CardHeader className="flex items-center justify-between">
          <CardTitle className="text-base">Per-person breakdown</CardTitle>
          {split.isFetching ? (
            <Loader2 className="text-muted-foreground h-3.5 w-3.5 animate-spin" />
          ) : null}
        </CardHeader>
        <CardContent>
          {split.isError ? (
            <p className="text-destructive text-sm" role="alert">
              Could not load cost split: {split.error?.message ?? "unknown error"}
            </p>
          ) : null}
          <table className="w-full text-sm" data-testid="cost-table">
            <thead>
              <tr className="text-muted-foreground border-b text-xs uppercase tracking-wider">
                <ThButton onClick={() => toggleSort("name")} active={sortKey === "name"} dir={sortDir} align="left">
                  Participant
                </ThButton>
                <ThButton onClick={() => toggleSort("fuel")} active={sortKey === "fuel"} dir={sortDir}>
                  Fuel
                </ThButton>
                <ThButton onClick={() => toggleSort("tolls")} active={sortKey === "tolls"} dir={sortDir}>
                  Tolls
                </ThButton>
                <ThButton onClick={() => toggleSort("amount")} active={sortKey === "amount"} dir={sortDir}>
                  Total
                </ThButton>
              </tr>
            </thead>
            <tbody className="divide-y">
              {rows.length === 0 ? (
                <tr>
                  <td colSpan={4} className="text-muted-foreground py-6 text-center text-sm">
                    No participants in this trip.
                  </td>
                </tr>
              ) : null}
              {rows.map((row) => (
                <tr key={row.participantId} data-testid="cost-row">
                  <td className="py-2.5">
                    <div className="flex items-center gap-2">
                      <span className="font-medium">{row.displayName}</span>
                      {row.isDriver ? <Badge variant="outline">Driver</Badge> : null}
                    </div>
                  </td>
                  <td className="py-2.5 text-right tabular-nums">
                    ${row.fuel.toFixed(2)}
                  </td>
                  <td className="py-2.5 text-right tabular-nums">
                    ${row.tolls.toFixed(2)}
                  </td>
                  <td className="py-2.5 text-right font-medium tabular-nums">
                    ${row.amount.toFixed(2)}
                  </td>
                </tr>
              ))}
            </tbody>
            <tfoot>
              <tr className="text-muted-foreground border-t text-xs">
                <td className="pt-3 text-right">Totals</td>
                <td className="pt-3 text-right tabular-nums">${totals.totalFuel.toFixed(2)}</td>
                <td className="pt-3 text-right tabular-nums">${totals.totalTolls.toFixed(2)}</td>
                <td className="pt-3 text-right font-semibold tabular-nums">
                  ${totals.total.toFixed(2)} {split.data?.currency ?? "AUD"}
                </td>
              </tr>
            </tfoot>
          </table>
          {totals.driversAtZero && driverIds.size > 0 ? (
            <>
              <Separator className="my-4" />
              <p className="text-emerald-600 dark:text-emerald-400 text-xs">
                Driver pays nothing — passengers cover fuel and tolls in proportion to the distance
                they were aboard.
              </p>
            </>
          ) : null}
          {split.data?.totalCost === 0 && !split.isFetching ? (
            <p className="text-muted-foreground mt-2 text-xs">
              Cost split returns zero — WS7&apos;s CostSplitService may not be wired up yet.
            </p>
          ) : null}
        </CardContent>
      </Card>
    </div>
  );
}

interface ThButtonProps {
  onClick: () => void;
  active: boolean;
  dir: SortDir;
  align?: "left" | "right";
  children: React.ReactNode;
}

function ThButton({
  onClick,
  active,
  dir,
  align = "right",
  children,
}: ThButtonProps): React.JSX.Element {
  return (
    <th className={`pb-2 ${align === "left" ? "text-left" : "text-right"}`}>
      <button
        type="button"
        onClick={onClick}
        className={`hover:text-foreground inline-flex items-center gap-1 font-medium uppercase tracking-wider ${active ? "text-foreground" : ""}`}
      >
        {children}
        {active ? <span aria-hidden>{dir === "asc" ? "↑" : "↓"}</span> : null}
      </button>
    </th>
  );
}
