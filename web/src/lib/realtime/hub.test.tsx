import { describe, expect, it, vi } from "vitest";
import { act, renderHook, waitFor } from "@testing-library/react";
import { HubConnectionState } from "@microsoft/signalr";
import {
  useTripHub,
  type HubConnectionLike,
  type DriverPositionPayload,
} from "./hub";

interface MockHandlers {
  onclose?: (err?: Error) => void;
  onreconnecting?: (err?: Error) => void;
  onreconnected?: (id?: string) => void;
}

interface MockHub extends HubConnectionLike {
  emit: (eventName: string, payload: unknown) => void;
  startSpy: ReturnType<typeof vi.fn>;
  invokeSpy: ReturnType<typeof vi.fn>;
  stopSpy: ReturnType<typeof vi.fn>;
  setState: (next: HubConnectionState) => void;
}

function createMockHub(opts: { startReject?: Error } = {}): MockHub {
  const eventHandlers = new Map<string, Set<(...a: unknown[]) => void>>();
  const meta: MockHandlers = {};
  const startSpy = vi.fn(async () => {
    if (opts.startReject) throw opts.startReject;
  });
  const invokeSpy = vi.fn<(...args: unknown[]) => Promise<unknown>>(
    async () => undefined,
  );
  const stopSpy = vi.fn(async () => undefined);
  let state: HubConnectionState = HubConnectionState.Disconnected;
  const hub: MockHub = {
    get state(): HubConnectionState {
      return state;
    },
    start: () => startSpy(),
    stop: () => stopSpy(),
    invoke: (method: string, ...args: unknown[]) => invokeSpy(method, ...args),
    on: (name, handler) => {
      let set = eventHandlers.get(name);
      if (!set) {
        set = new Set();
        eventHandlers.set(name, set);
      }
      set.add(handler);
    },
    off: (name, handler) => {
      const set = eventHandlers.get(name);
      if (!set) return;
      if (handler) set.delete(handler);
      else set.clear();
    },
    onclose: (h) => (meta.onclose = h),
    onreconnecting: (h) => (meta.onreconnecting = h),
    onreconnected: (h) => (meta.onreconnected = h),
    emit: (name, payload) => {
      eventHandlers.get(name)?.forEach((h) => h(payload));
    },
    startSpy,
    invokeSpy,
    stopSpy,
    setState: (next) => {
      state = next;
    },
  };
  return hub;
}

describe("useTripHub", () => {
  it("connects, joins, exposes status=connected", async () => {
    const mock = createMockHub();
    mock.startSpy.mockImplementation(async () => {
      mock.setState(HubConnectionState.Connected);
    });
    const factory = (): typeof mock => mock;
    const { result } = renderHook(() => useTripHub("trip-1", { connectionFactory: factory }));

    await waitFor(() => expect(result.current.status).toBe("connected"));
    expect(mock.startSpy).toHaveBeenCalled();
    expect(mock.invokeSpy).toHaveBeenCalledWith("JoinTripAsync", "trip-1");
  });

  it("surfaces start errors as disconnected with error", async () => {
    const mock = createMockHub({ startReject: new Error("CORS rejected") });
    const factory = (): typeof mock => mock;
    const { result } = renderHook(() => useTripHub("trip-1", { connectionFactory: factory }));
    await waitFor(() => expect(result.current.status).toBe("disconnected"));
    expect(result.current.error?.message).toBe("CORS rejected");
  });

  it("delivers DriverPositionUpdated to subscribers", async () => {
    const mock = createMockHub();
    mock.startSpy.mockImplementation(async () => {
      mock.setState(HubConnectionState.Connected);
    });
    const factory = (): typeof mock => mock;
    const { result } = renderHook(() => useTripHub("trip-1", { connectionFactory: factory }));
    await waitFor(() => expect(result.current.status).toBe("connected"));

    const received: DriverPositionPayload[] = [];
    let unsubscribe: () => void = () => undefined;
    act(() => {
      unsubscribe = result.current.onDriverPosition((p) => received.push(p));
    });

    act(() => {
      mock.emit("DriverPositionUpdated", {
        driverId: "d-1",
        lat: -33.86,
        lng: 151.2,
        ts: "2026-05-23T10:00:00Z",
      });
    });
    expect(received).toHaveLength(1);
    expect(received[0].lat).toBe(-33.86);

    unsubscribe();
    act(() => {
      mock.emit("DriverPositionUpdated", {
        driverId: "d-1",
        lat: 0,
        lng: 0,
        ts: "2026-05-23T10:00:05Z",
      });
    });
    expect(received).toHaveLength(1);
  });

  it("publishDriverPosition invokes server when connected", async () => {
    const mock = createMockHub();
    mock.startSpy.mockImplementation(async () => {
      mock.setState(HubConnectionState.Connected);
    });
    const factory = (): typeof mock => mock;
    const { result } = renderHook(() => useTripHub("trip-1", { connectionFactory: factory }));
    await waitFor(() => expect(result.current.status).toBe("connected"));

    await act(async () => {
      await result.current.publishDriverPosition("driver-1", -33.9, 151.18);
    });
    expect(mock.invokeSpy).toHaveBeenCalledWith(
      "PublishDriverPositionAsync",
      "trip-1",
      "driver-1",
      -33.9,
      151.18,
    );
  });

  it("stops the connection on unmount", async () => {
    const mock = createMockHub();
    mock.startSpy.mockImplementation(async () => {
      mock.setState(HubConnectionState.Connected);
    });
    const factory = (): typeof mock => mock;
    const { result, unmount } = renderHook(() =>
      useTripHub("trip-1", { connectionFactory: factory }),
    );
    await waitFor(() => expect(result.current.status).toBe("connected"));
    unmount();
    expect(mock.invokeSpy).toHaveBeenCalledWith("LeaveTripAsync", "trip-1");
    expect(mock.stopSpy).toHaveBeenCalled();
  });
});
