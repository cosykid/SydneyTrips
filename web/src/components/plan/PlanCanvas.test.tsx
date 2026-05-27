import { describe, expect, it, vi, beforeEach } from "vitest";
import { render, screen, waitFor, within } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { PlanCanvas } from "./PlanCanvas";
import type { components } from "@/lib/api/types";

type TripDetailDto = components["schemas"]["TripDetailDto"];

vi.mock("sonner", () => ({
  toast: { success: vi.fn(), error: vi.fn() },
  Toaster: () => null,
}));

// Stub the PlanMap so jsdom doesn't try to instantiate Google Maps.
vi.mock("./PlanMap", () => ({
  PlanMap: () => <div data-testid="map-stub" />,
}));

const trip: TripDetailDto = {
  id: "trip-1",
  name: "Test trip",
  destinationName: "Palm Beach NSW",
  destinationLongitude: 151.32,
  destinationLatitude: -33.6,
  departAt: new Date("2026-06-01T09:00:00Z").toISOString(),
  arrivalWindowEarliest: new Date("2026-06-01T09:15:00Z").toISOString(),
  arrivalWindowLatest: new Date("2026-06-01T09:45:00Z").toISOString(),
  ownerId: "00000000-0000-0000-0000-000000000000",
  createdAt: new Date("2026-05-01T00:00:00Z").toISOString(),
  lockedSolutionId: null,
  participants: [
    {
      id: "p-1",
      tripId: "trip-1",
      displayName: "Alex",
      homeLongitude: 151.2,
      homeLatitude: -33.86,
      hasCar: true,
      seats: 4,
      preferences: { walkBudgetMins: 15, detourToleranceMins: 10, fairnessWeight: 1 },
      candidateNodes: [
        {
          id: "n-1",
          participantId: "p-1",
          kind: "busStop",
          longitude: 151.21,
          latitude: -33.87,
          walkMins: 5,
          ptMins: 0,
          externalId: null,
          displayName: "Glebe Pt Rd Stop",
          path: null,
        },
      ],
    },
    {
      id: "p-2",
      tripId: "trip-1",
      displayName: "Blair",
      homeLongitude: 151.24,
      homeLatitude: -33.88,
      hasCar: false,
      seats: 0,
      preferences: { walkBudgetMins: 15, detourToleranceMins: 10, fairnessWeight: 1 },
      candidateNodes: [],
    },
  ],
};

// Wire shape from the API: { run: OptimisationRunDto, solution?: SolutionDto }.
// The run's `solution` is what the slider-driven planner shows now — the old Pareto
// carousel was replaced by a single-solution panel, so this response is the only
// source of truth for what's on screen.
const runResponse = {
  run: {
    id: "run-1",
    tripId: "trip-1",
    status: "completed" as const,
    solver: "orTools" as const,
    weights: { driveTime: 0.4, stopCount: 0.2, walkAndPt: 0.2, arrivalSpread: 0, fairness: 0.2 },
    startedAt: new Date().toISOString(),
    completedAt: new Date().toISOString(),
    failureReason: null,
    bestSolutionId: "sol-1",
    stats: null,
  },
  solution: {
    id: "sol-1",
    optimisationRunId: "run-1",
    label: "current",
    objective: 42,
    objectiveTerms: [],
    routes: [
      {
        id: "dr-1",
        driverId: "p-1",
        travelMins: 42,
        orderIndex: 0,
        destinationArrival: new Date("2026-06-01T09:50:00Z").toISOString(),
        departure: new Date("2026-06-01T09:08:00Z").toISOString(),
        stops: [
          {
            id: "stop-1",
            orderIndex: 0,
            longitude: 151.21,
            latitude: -33.87,
            candidateNodeId: "n-1",
            nodeKind: "busStop",
            estimatedArrival: new Date("2026-06-01T09:20:00Z").toISOString(),
            pickups: [
              {
                participantId: "p-2",
                walkMins: 5,
                ptMins: 12,
                path: null,
                pathLegs: null,
              },
            ],
          },
        ],
      },
    ],
  },
};

const mockApiCalls = vi.hoisted(() => ({
  fetchMock: vi.fn<(input: string, init?: RequestInit) => Promise<Response>>(),
}));

vi.mock("@/lib/api/client", async () => {
  const actual = await vi.importActual<typeof import("@/lib/api/client")>("@/lib/api/client");
  return {
    ...actual,
    apiFetch: async <T,>(path: string, init?: { method?: string }) => {
      const res = await mockApiCalls.fetchMock(path, init);
      if (!res.ok) throw new actual.ApiError(res.status, "fail", null);
      return (await res.json()) as T;
    },
  };
});

function jsonResponse(body: unknown, status = 200): Response {
  return new Response(JSON.stringify(body), {
    status,
    headers: { "content-type": "application/json" },
  });
}

beforeEach(() => {
  mockApiCalls.fetchMock.mockReset();
  mockApiCalls.fetchMock.mockImplementation(async (path) => {
    if (path === "/trips/trip-1") return jsonResponse(trip);
    if (path === "/trips/trip-1/optimise") return jsonResponse({ runId: "run-1" });
    if (path === "/trips/trip-1/runs/run-1") return jsonResponse(runResponse);
    if (path === "/trips/trip-1/lock-solution") return jsonResponse(trip);
    return jsonResponse({ message: `unhandled ${path}` }, 500);
  });
});

function renderCanvas(): void {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={qc}>
      <PlanCanvas tripId="trip-1" />
    </QueryClientProvider>,
  );
}

describe("PlanCanvas", () => {
  it("renders the planner header once trip data loads", async () => {
    renderCanvas();
    expect(await screen.findByText(/Test trip/)).toBeInTheDocument();
    expect(screen.getByTestId("map-stub")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /^Plan trip$/i })).toBeInTheDocument();
  });

  it("shows the slider-driven solution panel after optimisation completes", async () => {
    const user = userEvent.setup();
    renderCanvas();

    const optimise = await screen.findByRole("button", { name: /^Plan trip$/i });
    await user.click(optimise);

    await waitFor(() => expect(screen.getByTestId("solution-panel")).toBeInTheDocument());
    // "42 min" also appears as "Longest single drive" (one route), so anchor on the label.
    const longestDrive = screen.getByText("Longest single drive").parentElement!;
    expect(within(longestDrive).getByText(/42 min/)).toBeInTheDocument();
    const longestJourney = screen.getByText("Longest journey").parentElement!;
    expect(within(longestJourney).getByText(/Blair · 47 min/)).toBeInTheDocument();
    expect(screen.queryByText("Total driving")).not.toBeInTheDocument();
    expect(screen.queryByText("Total walking")).not.toBeInTheDocument();
    expect(screen.queryByText("Longest walk")).not.toBeInTheDocument();
    expect(screen.getByRole("button", { name: /^Re-plan$/i })).toBeInTheDocument();
  });
});
