import { describe, expect, it, vi, beforeEach } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { PlanCanvas } from "./PlanCanvas";
import type {
  ParetoResponse,
  RunSolutionResponse,
  Trip,
} from "@/lib/api/schema";

vi.mock("sonner", () => ({
  toast: { success: vi.fn(), error: vi.fn() },
  Toaster: () => null,
}));

// Stub the PlanMap so jsdom doesn't try to instantiate Mapbox GL.
vi.mock("./PlanMap", () => ({
  PlanMap: () => <div data-testid="map-stub" />,
}));

const trip: Trip = {
  id: "trip-1",
  name: "Test trip",
  destinationAddress: "Palm Beach NSW",
  destination: { lat: -33.6, lng: 151.32 },
  departAt: new Date("2026-06-01T09:00:00Z").toISOString(),
  arrivalWindowMinutes: 15,
  status: "draft",
  participantCount: 2,
  hasLockedSolution: false,
  participants: [
    {
      id: "p-1",
      displayName: "Alex",
      role: "driver",
      originAddress: "1 George St",
      origin: { lat: -33.86, lng: 151.2 },
      seatsAvailable: 4,
    },
  ],
  candidateNodes: [
    { id: "n-1", location: { lat: -33.87, lng: 151.21 }, modality: "bus_stop" },
  ],
};

const runResponse: RunSolutionResponse = {
  id: "run-1",
  tripId: "trip-1",
  status: "completed",
  createdAt: new Date().toISOString(),
};

const paretoResponse: ParetoResponse = {
  runId: "run-1",
  solutions: [
    {
      id: "sol-1",
      label: "fastest",
      metrics: {
        totalDrivingMinutes: 42,
        maxDrivingMinutes: 32,
        totalStops: 3,
        totalWalkMetres: 450,
        maxWalkMetres: 220,
        fairnessIndex: 0.84,
      },
      routes: [
        {
          driverParticipantId: "p-1",
          driverDisplayName: "Alex",
          colour: "#0072B2",
          polyline: [
            { lat: -33.86, lng: 151.2 },
            { lat: -33.7, lng: 151.27 },
          ],
          stops: [],
          drivingMinutes: 42,
          drivingDistanceKm: 35,
        },
      ],
    },
  ],
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
    if (path === "/trips/trip-1/runs/run-1/pareto") return jsonResponse(paretoResponse);
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
    expect(await screen.findByText("Planner")).toBeInTheDocument();
    expect(await screen.findByText(/Test trip/)).toBeInTheDocument();
    expect(screen.getByTestId("map-stub")).toBeInTheDocument();
    expect(screen.getByRole("button", { name: /^Optimise$/i })).toBeInTheDocument();
  });

  it("transitions to the Pareto carousel after optimisation completes", async () => {
    const user = userEvent.setup();
    renderCanvas();

    const optimise = await screen.findByRole("button", { name: /^Optimise$/i });
    await user.click(optimise);

    await waitFor(() =>
      expect(screen.getByTestId("pareto-carousel")).toBeInTheDocument(),
    );
    expect(screen.getByText(/42 min/)).toBeInTheDocument(); // total driving
    expect(screen.getByRole("button", { name: /Re-run optimisation/i })).toBeInTheDocument();
  });
});
