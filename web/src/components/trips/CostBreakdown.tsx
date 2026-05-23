"use client";

import { Card, CardContent, CardHeader, CardTitle } from "@/components/ui/card";
import { Loader2 } from "lucide-react";
import { useCostSplit, useTrip } from "@/lib/api/hooks";

export function CostBreakdown({ tripId }: { tripId: string }): React.JSX.Element {
  const trip = useTrip(tripId);
  const split = useCostSplit(tripId);

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

  const data = split.data;
  const participants = trip.data.participants;
  const rows = data?.perParticipant.length
    ? data.perParticipant
    : participants.map((p) => ({
        participantId: p.id,
        displayName: p.displayName,
        amount: 0,
        breakdown: { fuel: 0, tolls: 0 },
      }));

  return (
    <Card>
      <CardHeader>
        <CardTitle className="text-base">Per-person breakdown</CardTitle>
      </CardHeader>
      <CardContent>
        <table className="w-full text-sm">
          <thead>
            <tr className="text-muted-foreground text-xs uppercase tracking-wider">
              <th className="py-2 text-left font-medium">Participant</th>
              <th className="py-2 text-right font-medium">Fuel</th>
              <th className="py-2 text-right font-medium">Tolls</th>
              <th className="py-2 text-right font-medium">Total</th>
            </tr>
          </thead>
          <tbody className="divide-y">
            {rows.map((row) => (
              <tr key={row.participantId}>
                <td className="py-2.5">{row.displayName}</td>
                <td className="py-2.5 text-right tabular-nums">
                  ${row.breakdown.fuel.toFixed(2)}
                </td>
                <td className="py-2.5 text-right tabular-nums">
                  ${row.breakdown.tolls.toFixed(2)}
                </td>
                <td className="py-2.5 text-right tabular-nums font-medium">
                  ${row.amount.toFixed(2)}
                </td>
              </tr>
            ))}
          </tbody>
          <tfoot>
            <tr className="text-muted-foreground text-xs">
              <td colSpan={3} className="pt-3 text-right">
                Total
              </td>
              <td className="pt-3 text-right font-semibold">
                ${(data?.totalCost ?? 0).toFixed(2)} {data?.currency ?? "AUD"}
              </td>
            </tr>
          </tfoot>
        </table>
        <p className="text-muted-foreground mt-4 text-xs">
          Numbers are placeholder zeros until WS7 wires up the CostSplitService.
        </p>
      </CardContent>
    </Card>
  );
}
