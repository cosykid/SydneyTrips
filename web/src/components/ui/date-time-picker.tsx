"use client";

import * as React from "react";
import {
  addDays,
  addMonths,
  endOfMonth,
  format,
  isSameDay,
  isSameMonth,
  startOfMonth,
  startOfWeek,
  subDays,
  subMonths,
} from "date-fns";
import { Calendar, ChevronLeft, ChevronRight, Clock } from "lucide-react";

import { cn } from "@/lib/utils";
import { Popover, PopoverContent, PopoverTrigger } from "@/components/ui/popover";

/**
 * Google-Maps-style date/time picker. Two adjacent pills — time on the left
 * (clock icon, opens a 30-minute slot list), date on the right (calendar
 * icon, opens a month grid). Each pill has matching ±15-min / ±1-day arrow
 * buttons so quick adjustments don't require opening the popover. The value
 * round-trips as the same `YYYY-MM-DDTHH:mm` string the native datetime-local
 * input emits, so it drops straight into the existing form.
 */
export interface DateTimePickerProps {
  /** Local-time ISO-ish string: `YYYY-MM-DDTHH:mm`. */
  value: string;
  onChange: (next: string) => void;
  id?: string;
}

export function DateTimePicker({
  value,
  onChange,
  id,
}: DateTimePickerProps): React.JSX.Element {
  const current = parseLocal(value);

  function emit(next: Date): void {
    onChange(formatLocal(next));
  }

  return (
    <div className="flex items-stretch gap-2" id={id}>
      <TimeField value={current} onChange={emit} />
      <DateField value={current} onChange={emit} />
    </div>
  );
}

// ─── time pill ──────────────────────────────────────────────────────────────

function TimeField({
  value,
  onChange,
}: {
  value: Date;
  onChange: (d: Date) => void;
}): React.JSX.Element {
  const slots = React.useMemo(() => buildTimeSlots(), []);
  const triggerLabel = format(value, "h:mm a").toLowerCase();
  // Highlight the slot closest to the current time when the popover opens.
  const activeSlotKey = nearestSlotKey(value);
  const listRef = React.useRef<HTMLDivElement>(null);

  return (
    <Popover>
      <Field>
        <FieldIcon>
          <Clock className="h-4 w-4" />
        </FieldIcon>
        <PopoverTrigger
          render={
            <button
              type="button"
              className="text-foreground hover:bg-muted/60 -my-1 -ml-1 cursor-pointer rounded-md px-2 py-1 text-sm font-medium tabular-nums focus:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            />
          }
        >
          {triggerLabel}
        </PopoverTrigger>
        <FieldArrows
          onPrev={() => onChange(addMinutes(value, -15))}
          onNext={() => onChange(addMinutes(value, 15))}
          prevLabel="15 minutes earlier"
          nextLabel="15 minutes later"
        />
      </Field>
      <PopoverContent
        align="start"
        className="w-32 py-1"
        // Scroll the active slot into the middle of the *inner* list on open.
        // `scrollIntoView({ block: "center" })` would also scroll every
        // ancestor scroll container (page, Card) and shove the layout, so we
        // set scrollTop on the list directly.
        onAnimationStart={() => {
          const list = listRef.current;
          const node = list?.querySelector<HTMLElement>(
            `[data-slot-key="${activeSlotKey}"]`,
          );
          if (list && node) {
            list.scrollTop =
              node.offsetTop - list.clientHeight / 2 + node.offsetHeight / 2;
          }
        }}
      >
        <div ref={listRef} className="max-h-72 overflow-y-auto py-1">
          {slots.map((slot) => {
            const active = slot.key === activeSlotKey;
            return (
              <button
                key={slot.key}
                data-slot-key={slot.key}
                type="button"
                onClick={() => {
                  const next = new Date(value);
                  next.setHours(slot.hour, slot.minute, 0, 0);
                  onChange(next);
                }}
                className={cn(
                  "hover:bg-muted block w-full px-4 py-2 text-left text-sm tabular-nums",
                  active && "bg-accent text-accent-foreground font-medium",
                )}
              >
                {slot.label}
              </button>
            );
          })}
        </div>
      </PopoverContent>
    </Popover>
  );
}

// ─── date pill ──────────────────────────────────────────────────────────────

function DateField({
  value,
  onChange,
}: {
  value: Date;
  onChange: (d: Date) => void;
}): React.JSX.Element {
  return (
    <Popover>
      <Field>
        <FieldIcon>
          <Calendar className="h-4 w-4" />
        </FieldIcon>
        <PopoverTrigger
          render={
            <button
              type="button"
              className="text-foreground hover:bg-muted/60 -my-1 -ml-1 cursor-pointer rounded-md px-2 py-1 text-sm font-medium focus:outline-none focus-visible:ring-2 focus-visible:ring-ring"
            />
          }
        >
          {format(value, "EEE, d MMM")}
        </PopoverTrigger>
        <FieldArrows
          onPrev={() => onChange(subDays(value, 1))}
          onNext={() => onChange(addDays(value, 1))}
          prevLabel="Previous day"
          nextLabel="Next day"
        />
      </Field>
      <PopoverContent align="end" className="w-72 p-3">
        <MonthGrid value={value} onChange={onChange} />
      </PopoverContent>
    </Popover>
  );
}

function MonthGrid({
  value,
  onChange,
}: {
  value: Date;
  onChange: (d: Date) => void;
}): React.JSX.Element {
  const [cursor, setCursor] = React.useState(() => startOfMonth(value));
  // 6 weeks × 7 days = 42 cells, fixed grid (mirrors Google Maps so the
  // popover height doesn't jump between months).
  const cells = React.useMemo(() => buildMonthCells(cursor), [cursor]);

  return (
    <div className="text-sm">
      <div className="mb-2 flex items-center justify-between">
        <button
          type="button"
          aria-label="Previous month"
          onClick={() => setCursor(subMonths(cursor, 1))}
          className="hover:bg-muted text-muted-foreground hover:text-foreground rounded-full p-1.5"
        >
          <ChevronLeft className="h-4 w-4" />
        </button>
        <div className="text-foreground font-medium">
          {format(cursor, "MMMM yyyy")}
        </div>
        <button
          type="button"
          aria-label="Next month"
          onClick={() => setCursor(addMonths(cursor, 1))}
          className="hover:bg-muted text-muted-foreground hover:text-foreground rounded-full p-1.5"
        >
          <ChevronRight className="h-4 w-4" />
        </button>
      </div>
      <div className="text-muted-foreground mb-1 grid grid-cols-7 text-center text-[11px] font-medium">
        {["Mon", "Tue", "Wed", "Thu", "Fri", "Sat", "Sun"].map((d) => (
          <div key={d} className="py-1">
            {d}
          </div>
        ))}
      </div>
      <div className="grid grid-cols-7 gap-y-0.5">
        {cells.map((day) => {
          const selected = isSameDay(day, value);
          const inMonth = isSameMonth(day, cursor);
          return (
            <button
              key={day.toISOString()}
              type="button"
              onClick={() => {
                const next = new Date(value);
                next.setFullYear(day.getFullYear(), day.getMonth(), day.getDate());
                onChange(next);
              }}
              className={cn(
                "mx-auto flex h-9 w-9 items-center justify-center rounded-full text-sm tabular-nums",
                "hover:bg-muted",
                !inMonth && "text-muted-foreground/60",
                selected && "bg-primary text-primary-foreground hover:bg-primary",
              )}
            >
              {day.getDate()}
            </button>
          );
        })}
      </div>
    </div>
  );
}

// ─── shared field chrome ────────────────────────────────────────────────────

function Field({ children }: { children: React.ReactNode }): React.JSX.Element {
  return (
    <div className="border-input hover:border-foreground/40 focus-within:border-ring focus-within:ring-ring/30 inline-flex h-9 items-center gap-1.5 rounded-md border bg-background px-2.5 transition-colors focus-within:ring-2">
      {children}
    </div>
  );
}

function FieldIcon({ children }: { children: React.ReactNode }): React.JSX.Element {
  return <span className="text-muted-foreground shrink-0">{children}</span>;
}

function FieldArrows({
  onPrev,
  onNext,
  prevLabel,
  nextLabel,
}: {
  onPrev: () => void;
  onNext: () => void;
  prevLabel: string;
  nextLabel: string;
}): React.JSX.Element {
  return (
    <span className="ml-1 inline-flex items-center gap-0.5">
      <button
        type="button"
        aria-label={prevLabel}
        onClick={onPrev}
        className="hover:bg-muted text-muted-foreground hover:text-foreground rounded p-0.5"
      >
        <ChevronLeft className="h-3.5 w-3.5" />
      </button>
      <button
        type="button"
        aria-label={nextLabel}
        onClick={onNext}
        className="hover:bg-muted text-muted-foreground hover:text-foreground rounded p-0.5"
      >
        <ChevronRight className="h-3.5 w-3.5" />
      </button>
    </span>
  );
}

// ─── helpers ────────────────────────────────────────────────────────────────

interface TimeSlot {
  key: string;
  hour: number;
  minute: number;
  label: string;
}

function buildTimeSlots(): TimeSlot[] {
  const slots: TimeSlot[] = [];
  for (let h = 0; h < 24; h += 1) {
    for (const m of [0, 30]) {
      const probe = new Date();
      probe.setHours(h, m, 0, 0);
      slots.push({
        key: `${h}:${m}`,
        hour: h,
        minute: m,
        label: format(probe, "h:mm a").toLowerCase(),
      });
    }
  }
  return slots;
}

function nearestSlotKey(value: Date): string {
  // Round down to the previous 30-minute mark so the highlighted row
  // matches the slot list's discrete buckets.
  const bucketMin = value.getMinutes() < 30 ? 0 : 30;
  return `${value.getHours()}:${bucketMin}`;
}

function buildMonthCells(cursor: Date): Date[] {
  // 42 cells starting from the Monday on/before the 1st of the month.
  const first = startOfMonth(cursor);
  const last = endOfMonth(cursor);
  const gridStart = startOfWeek(first, { weekStartsOn: 1 });
  const cells: Date[] = [];
  let day = gridStart;
  while (cells.length < 42) {
    cells.push(day);
    day = addDays(day, 1);
    if (cells.length >= 35 && day > last && day.getDay() === 1) break;
  }
  // If we exited early (5-row month), pad to 42 to keep height stable.
  while (cells.length < 42) {
    cells.push(day);
    day = addDays(day, 1);
  }
  return cells;
}

function addMinutes(d: Date, mins: number): Date {
  const next = new Date(d);
  next.setMinutes(next.getMinutes() + mins);
  return next;
}

// `datetime-local` uses local-time `YYYY-MM-DDTHH:mm` with no timezone
// suffix; `new Date(str)` parses that as local time, which is what we want.
function parseLocal(value: string): Date {
  const d = new Date(value);
  if (Number.isNaN(d.getTime())) return new Date();
  return d;
}

function formatLocal(d: Date): string {
  const pad = (n: number): string => String(n).padStart(2, "0");
  return `${d.getFullYear()}-${pad(d.getMonth() + 1)}-${pad(d.getDate())}T${pad(d.getHours())}:${pad(d.getMinutes())}`;
}
