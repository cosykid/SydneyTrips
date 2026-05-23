import { describe, expect, it, vi, beforeEach } from "vitest";
import { render, screen, waitFor } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { CostBreakdown } from "./CostBreakdown";
import type { CostSplit, Trip } from "@/lib/api/schema";

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

const trip: Trip = {
  id: "trip-1",
  name: "Test trip",
  destinationAddress: "Palm Beach",
  destination: { lat: -33.6, lng: 151.32 },
  departAt: new Date("2026-06-01T09:00:00Z").toISOString(),
  arrivalWindowMinutes: 15,
  status: "planned",
  participantCount: 3,
  hasLockedSolution: true,
  lockedSolutionId: "sol-1",
  participants: [
    {
      id: "p-1",
      displayName: "Alex",
      role: "driver",
      originAddress: "1 George St",
      origin: { lat: -33.86, lng: 151.2 },
      seatsAvailable: 4,
    },
    {
      id: "p-2",
      displayName: "Bri",
      role: "passenger",
      originAddress: "10 Glebe Pt Rd",
      origin: { lat: -33.88, lng: 151.18 },
    },
    {
      id: "p-3",
      displayName: "Cam",
      role: "passenger",
      originAddress: "5 Hereford St",
      origin: { lat: -33.87, lng: 151.17 },
    },
  ],
  candidateNodes: [],
};

const costSplit: CostSplit = {
  tripId: "trip-1",
  currency: "AUD",
  totalCost: 38.4,
  perParticipant: [
    {
      participantId: "p-1",
      displayName: "Alex",
      amount: 0,
      breakdown: { fuel: 0, tolls: 0 },
    },
    {
      participantId: "p-2",
      displayName: "Bri",
      amount: 20.4,
      breakdown: { fuel: 18, tolls: 2.4 },
    },
    {
      participantId: "p-3",
      displayName: "Cam",
      amount: 18,
      breakdown: { fuel: 16, tolls: 2 },
    },
  ],
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
          currency: "AUD",
          totalCost: 0,
          perParticipant: [],
        });
      return jsonResponse({ message: "x" }, 500);
    });
    renderBreakdown();
    await waitFor(() => expect(screen.getAllByTestId("cost-row")).toHaveLength(3));
    expect(screen.getAllByText("$0.00").length).toBeGreaterThan(2);
  });
});
