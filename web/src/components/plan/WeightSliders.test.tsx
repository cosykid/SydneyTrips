import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { WeightSliders } from "./WeightSliders";
import { DEFAULT_WEIGHTS } from "@/lib/api/schema";

describe("WeightSliders", () => {
  it("renders one slider per objective with the formatted value", () => {
    render(
      <WeightSliders
        weights={DEFAULT_WEIGHTS}
        onChange={() => {}}
        onReset={() => {}}
      />,
    );

    expect(screen.getByText("Driving time")).toBeInTheDocument();
    expect(screen.getByText("Stops")).toBeInTheDocument();
    expect(screen.getByText("Walking")).toBeInTheDocument();
    expect(screen.getByText("Fairness")).toBeInTheDocument();

    // Default driving-time weight is 0.40 — shown to 2 decimal places.
    expect(screen.getByText("0.40")).toBeInTheDocument();
  });

  it("calls onReset when the user clicks reset", async () => {
    const onReset = vi.fn();
    const user = userEvent.setup();
    render(
      <WeightSliders
        weights={DEFAULT_WEIGHTS}
        onChange={() => {}}
        onReset={onReset}
      />,
    );

    await user.click(screen.getByTestId("reset-weights"));
    expect(onReset).toHaveBeenCalledOnce();
  });
});
