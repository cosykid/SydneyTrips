import { describe, expect, it, vi } from "vitest";
import { render, screen } from "@testing-library/react";
import userEvent from "@testing-library/user-event";
import { QueryClient, QueryClientProvider } from "@tanstack/react-query";
import { ParticipantList } from "./ParticipantList";
import type { Participant } from "@/lib/api/schema";

vi.mock("sonner", () => ({
  toast: {
    success: vi.fn(),
    error: vi.fn(),
  },
  Toaster: () => null,
}));

const baseParticipants: Participant[] = [
  {
    id: "p-1",
    displayName: "Alex",
    role: "driver",
    originAddress: "1 George St, Sydney",
    origin: { lat: -33.86, lng: 151.2 },
    seatsAvailable: 4,
  },
  {
    id: "p-2",
    displayName: "Bri",
    role: "passenger",
    originAddress: "10 Glebe Pt Rd, Glebe",
    origin: { lat: -33.88, lng: 151.18 },
  },
];

function renderWithClient(ui: React.ReactNode): void {
  const qc = new QueryClient({ defaultOptions: { queries: { retry: false } } });
  render(<QueryClientProvider client={qc}>{ui}</QueryClientProvider>);
}

describe("ParticipantList", () => {
  it("renders existing participants with their role and address", () => {
    renderWithClient(<ParticipantList tripId="t-1" participants={baseParticipants} />);

    expect(screen.getByText("Alex")).toBeInTheDocument();
    expect(screen.getByText(/driver · 4 seats/i)).toBeInTheDocument();
    expect(screen.getByText("Bri")).toBeInTheDocument();
    // "passenger" appears in both the role <Select> trigger and Bri's badge,
    // so target the badge specifically by walking up from Bri.
    const briRow = screen.getByText("Bri").closest("li");
    expect(briRow).not.toBeNull();
    expect(briRow!).toHaveTextContent(/passenger/i);
    expect(screen.getByText("1 George St, Sydney")).toBeInTheDocument();
  });

  it("disables the seats field for passenger role", async () => {
    const user = userEvent.setup();
    renderWithClient(<ParticipantList tripId="t-1" participants={[]} />);

    const seatsInput = screen.getByLabelText(/seats/i) as HTMLInputElement;
    // Default role is passenger → seats disabled.
    expect(seatsInput).toBeDisabled();

    // Switch role to driver via the combobox. Base-ui Select renders a button
    // that opens a listbox; clicking through ensures the seats field enables.
    await user.click(screen.getByLabelText(/role/i));
    await user.click(screen.getByRole("option", { name: /driver/i }));
    expect(seatsInput).not.toBeDisabled();
  });

  it("shows an empty-state hint when there are no participants", () => {
    renderWithClient(<ParticipantList tripId="t-1" participants={[]} />);

    expect(screen.getByText(/no participants yet/i)).toBeInTheDocument();
  });
});
