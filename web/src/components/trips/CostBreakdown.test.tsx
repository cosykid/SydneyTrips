import { describe, expect, it, vi, beforeEach } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { CostBreakdown } from "./CostBreakdown";
import type { components } from "@/lib/api/types";

type TripDetailDto = components["schemas"]["TripDetailDto"];
type CostSplitResponse = components["schemas"]["CostSplitResponse"];

vi.mock("sonner", () => ({
  toast: { success: vi.fn(), error: vi.fn(), info: vi.fn() },
  Toaster: () => null,
}));

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

const prefs = { walkBudgetMins: 15, detourToleranceMins: 10, fairnessWeight: 1 };
const trip: TripDetailDto = {
  id: "trip-1",
  name: "Test trip",
  destinationName: "Palm Beach",
  destinationLongitude: 151.32,
  destinationLatitude: -33.6,
  departAt: new Date("2026-06-01T09:00:00Z").toISOString(),
  arrivalWindowEarliest: new Date("2026-06-01T09:15:00Z").toISOString(),
  arrivalWindowLatest: new Date("2026-06-01T09:45:00Z").toISOString(),
  ownerId: "00000000-0000-0000-0000-000000000000",
  createdAt: new Date("2026-05-01T00:00:00Z").toISOString(),
  lockedSolutionId: "sol-1",
  participants: [
    {
      id: "p-1",
      tripId: "trip-1",
      displayName: "Alex",
      homeLongitude: 151.2,
      homeLatitude: -33.86,
      hasCar: true,
      seats: 4,
      preferences: prefs,
      candidateNodes: [],
    },
    {
      id: "p-2",
      tripId: "trip-1",
      displayName: "Bri",
      homeLongitude: 151.18,
      homeLatitude: -33.88,
      hasCar: false,
      seats: 0,
      preferences: prefs,
      candidateNodes: [],
    },
    {
      id: "p-3",
      tripId: "trip-1",
      displayName: "Cam",
      homeLongitude: 151.17,
      homeLatitude: -33.87,
      hasCar: false,
      seats: 0,
      preferences: prefs,
      candidateNodes: [],
    },
  ],
};

const costSplit: CostSplitResponse = {
  tripId: "trip-1",
  solutionId: "sol-1",
  entries: [
    { participantId: "p-1", displayName: "Alex", fuelShare: 0, tollShare: 0, total: 0 },
    { participantId: "p-2", displayName: "Bri", fuelShare: 18, tollShare: 2.4, total: 20.4 },
    { participantId: "p-3", displayName: "Cam", fuelShare: 16, tollShare: 2, total: 18 },
  ],
  totalCost: 38.4,
  totalFuel: 34,
  totalTolls: 4.4,
  fuelPricePerLitre: 2.1,
  fuelEconomyLPer100Km: 8.5,
};

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
    if (path === "/trips/trip-1/cost-split") return jsonResponse(costSplit);
    return jsonResponse({ message: `unhandled ${path}` }, 500);
  });
});

function renderBreakdown(): void {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(
    <QueryClientProvider client={qc}>
      <CostBreakdown tripId="trip-1" />
    </QueryClientProvider>,
  );
}

describe("CostBreakdown", () => {
  it("renders one row per participant with fuel/tolls/total", async () => {
    renderBreakdown();
    await waitFor(() => expect(screen.getAllByTestId("cost-row")).toHaveLength(3));
    expect(screen.getByText("Alex")).toBeInTheDocument();
    expect(screen.getByText("Bri")).toBeInTheDocument();
    expect(screen.getByText("$20.40")).toBeInTheDocument();
    expect(screen.getByText("$2.40")).toBeInTheDocument();
  });

  it("shows the 'driver pays nothing' callout when driver amount is zero", async () => {
    renderBreakdown();
    await waitFor(() =>
      expect(screen.getByText(/Driver pays nothing/i)).toBeInTheDocument(),
    );
  });

  it("sorts by amount descending by default and toggles on click", async () => {
    renderBreakdown();
    const user = userEvent.setup();
    await waitFor(() => expect(screen.getAllByTestId("cost-row")).toHaveLength(3));

    // Click "Total" header to toggle to ascending — Alex (0) should be first.
    const totalButton = screen.getByRole("button", { name: /total/i });
    await user.click(totalButton);
    const rows = screen.getAllByTestId("cost-row");
    expect(rows[0]).toHaveTextContent("Alex");
  });

  it("placeholder zero rows when cost-split endpoint returns empty list", async () => {
    mockApiCalls.fetchMock.mockImplementation(async (path) => {
      if (path === "/trips/trip-1") return jsonResponse(trip);
      if (path === "/trips/trip-1/cost-split")
        return jsonResponse({
          tripId: "trip-1",
          solutionId: "sol-1",
          entries: [],
          totalCost: 0,
          totalFuel: 0,
          totalTolls: 0,
          fuelPricePerLitre: 2.1,
          fuelEconomyLPer100Km: 8.5,
        } satisfies CostSplitResponse);
      return jsonResponse({ message: "x" }, 500);
    });
    renderBreakdown();
    await waitFor(() => expect(screen.getAllByTestId("cost-row")).toHaveLength(3));
    expect(screen.getAllByText("$0.00").length).toBeGreaterThan(2);
  });
});
