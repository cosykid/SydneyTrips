import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { WeightSliders } from "./WeightSliders";
import { DEFAULT_WEIGHTS } from "@/lib/api/schema";

describe("WeightSliders", () => {
  it("renders one slider per route-priority dimension with the formatted value", () => {
    render(
      <WeightSliders
        weights={DEFAULT_WEIGHTS}
        onChange={() => {}}
        onReset={() => {}}
      />,
    );

    expect(screen.getByText("Driving time")).toBeInTheDocument();
    expect(screen.getByText("Number of stops")).toBeInTheDocument();
    expect(screen.getByText("Walking distance")).toBeInTheDocument();
    expect(screen.getByText("Fair sharing")).toBeInTheDocument();

    // Default driving-time weight is 0.40 — shown as a percentage.
    expect(screen.getByText("40%")).toBeInTheDocument();
  });

  it("calls onReset when the user clicks reset", async () => {
    const onReset = vi.fn();
    const user = userEvent.setup();
    render(
      <WeightSliders
        weights={{ ...DEFAULT_WEIGHTS, drivingTime: 0.5, stops: 0.3, walking: 0.1, fairness: 0.1 }}
        onChange={() => {}}
        onReset={onReset}
      />,
    );

    await user.click(screen.getByTestId("reset-weights"));
    expect(onReset).toHaveBeenCalledOnce();
  });
});
