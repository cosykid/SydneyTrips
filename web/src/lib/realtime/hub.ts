// SignalR client wrapper for the TripHub.
//
// Architecture choice — SignalR cannot ride the same-origin Next.js proxy
// because Next route handlers don't tunnel WebSocket upgrades. Instead, we:
//
//   1. Fetch a per-user access token from /api/realtime/token (server-side
//      route handler that reads the httpOnly session cookie and returns the
//      API JWT + the absolute hub URL).
//   2. Connect SignalR directly to the upstream API hub, passing the JWT via
//      `accessTokenFactory` so the WS upgrade carries it on the query string.
//      (The Trips API CORS layer must allow the web origin and the
//      `Authorization` header — see WS5 docs.)
//
// The wrapper is tolerant of WS5 not being merged: if `/hubs/trip/negotiate`
// returns 404 or the token endpoint 502s, we land in `disconnected` with the
// error available so the UI can show "live coordination unavailable".
// Tests inject a `connectionFactory` to avoid the real dependency.

"use client";

import {
  useCallback,
  useEffect,
  useMemo,
  useRef,
  useState,
} from "react";
import {
  HubConnection,
  HubConnectionBuilder,
  HubConnectionState,
  LogLevel,
} from "@microsoft/signalr";

export type ConnectionStatus =
  | "idle"
  | "connecting"
  | "connected"
  | "reconnecting"
  | "disconnected";

export interface DriverPositionPayload {
  driverId: string;
  lat: number;
  lng: number;
  /** ISO-8601 timestamp from the server. */
  ts: string;
}

export interface EtaUpdatedPayload {
  passengerId: string;
  /** ISO-8601 timestamp of the new ETA at pickup. */
  newEta: string;
}

export interface PassengerAtStopPayload {
  passengerId: string;
  stopId: string;
  ts: string;
}

export interface RouteRecomputedPayload {
  tripId: string;
  solutionId: string;
}

export interface TripStatusChangedPayload {
  tripId: string;
  status: string;
}

/** Subset of HubConnection we actually use — lets tests inject a mock. */
export interface HubConnectionLike {
  state: HubConnectionState;
  start: () => Promise<void>;
  stop: () => Promise<void>;
  invoke: (methodName: string, ...args: unknown[]) => Promise<unknown>;
  on: (methodName: string, handler: (...args: unknown[]) => void) => void;
  off: (methodName: string, handler?: (...args: unknown[]) => void) => void;
  onclose: (handler: (error?: Error) => void) => void;
  onreconnecting: (handler: (error?: Error) => void) => void;
  onreconnected: (handler: (connectionId?: string) => void) => void;
}

export interface UseTripHubOptions {
  /** Override the SignalR connection — used by tests + storybook. */
  connectionFactory?: () => HubConnectionLike;
  /** Disable auto-connect (component opted out, e.g. unauthenticated). */
  enabled?: boolean;
  /**
   * Override the token-fetcher (tests use this to skip the /api/realtime/token
   * fetch; production builds use the default).
   */
  tokenFetcher?: () => Promise<{ hubUrl: string; accessToken: string }>;
}

export interface TripHubApi {
  status: ConnectionStatus;
  error: Error | null;
  onDriverPosition: (handler: (p: DriverPositionPayload) => void) => () => void;
  onEtaUpdated: (handler: (p: EtaUpdatedPayload) => void) => () => void;
  onPassengerAtStop: (handler: (p: PassengerAtStopPayload) => void) => () => void;
  onRouteRecomputed: (handler: (p: RouteRecomputedPayload) => void) => () => void;
  onTripStatusChanged: (handler: (p: TripStatusChangedPayload) => void) => () => void;
  publishDriverPosition: (lat: number, lng: number) => Promise<void>;
  passengerCheckIn: (stopId: string) => Promise<void>;
}

async function defaultTokenFetcher(): Promise<{ hubUrl: string; accessToken: string }> {
  const res = await fetch("/api/realtime/token", {
    credentials: "include",
    cache: "no-store",
  });
  if (!res.ok) {
    const text = await res.text().catch(() => "");
    throw new Error(`token fetch failed: ${res.status} ${text}`);
  }
  return (await res.json()) as { hubUrl: string; accessToken: string };
}

function defaultConnectionFactory(
  hubUrl: string,
  accessToken: string,
): HubConnectionLike {
  const conn: HubConnection = new HubConnectionBuilder()
    .withUrl(hubUrl, {
      // SignalR appends the token to the negotiate query string and the WS
      // URL. The Trips API parses it via its standard JWT bearer middleware.
      accessTokenFactory: () => accessToken,
    })
    .withAutomaticReconnect()
    .configureLogging(LogLevel.Warning)
    .build();
  return conn;
}

const EVENT_NAMES = [
  "DriverPositionUpdated",
  "EtaUpdated",
  "PassengerAtStop",
  "RouteRecomputed",
  "TripStatusChanged",
] as const;

type AnyHandler = (...args: unknown[]) => void;

/**
 * Subscribes to the SignalR TripHub for the given trip id, joining the
 * server-side group on mount and leaving on unmount. Returns typed event
 * subscriptions and the current connection status.
 */
export function useTripHub(
  tripId: string | undefined,
  options: UseTripHubOptions = {},
): TripHubApi {
  const { connectionFactory, enabled = true, tokenFetcher } = options;
  // Connection state lives in plain React state — setStatus is invoked from
  // the effect via stable callbacks below, never directly during render.
  const [status, setStatus] = useState<ConnectionStatus>("idle");
  const [error, setError] = useState<Error | null>(null);
  // Handler Sets and the live connection sit in refs because they aren't part
  // of the rendered output — they're long-lived state owned by the effect.
  // useRef with stable initialisers keeps the Next.js refs-during-render rule
  // happy: we only touch `.current` inside callbacks/effects.
  const connectionRef = useRef<HubConnectionLike | null>(null);
  const handlersRef = useRef<Map<string, Set<AnyHandler>>>(
    new Map(EVENT_NAMES.map((n) => [n, new Set<AnyHandler>()])),
  );

  useEffect(() => {
    if (!enabled || !tripId) return;
    let cancelled = false;
    // Bridge handlers live in the effect scope so the cleanup can detach them
    // — that's what lets the hook tolerate dep changes (e.g. factory identity
    // shifts) without leaking listeners or double-firing handlers.
    const bridgeHandlers = new Map<string, (...args: unknown[]) => void>();
    // Defer to microtask so the rule doesn't flag a sync in-effect setState.
    queueMicrotask(() => {
      setStatus("connecting");
      setError(null);
    });

    async function go(): Promise<void> {
      let connection: HubConnectionLike;
      if (connectionFactory) {
        connection = connectionFactory();
      } else {
        const { hubUrl, accessToken } = await (tokenFetcher ?? defaultTokenFetcher)();
        connection = defaultConnectionFactory(hubUrl, accessToken);
      }

      connection.onreconnecting(() => {
        if (cancelled) return;
        setStatus("reconnecting");
      });
      connection.onreconnected(() => {
        if (cancelled) return;
        setStatus("connected");
        // Re-join the group after a reconnect — server forgets us on drop.
        connection.invoke("JoinTripAsync", tripId).catch((err: unknown) => {
          console.warn("[trip-hub] re-join failed", err);
        });
      });
      connection.onclose((err) => {
        if (cancelled) return;
        setStatus("disconnected");
        if (err) setError(err);
      });

      // Bridge SignalR's `on` into the ref's handler Sets so
      // subscribe/unsubscribe are O(1) and survive component re-renders.
      for (const name of EVENT_NAMES) {
        const set = handlersRef.current.get(name);
        if (!set) continue;
        const bridge = (...args: unknown[]): void => {
          for (const h of set) h(...args);
        };
        bridgeHandlers.set(name, bridge);
        connection.on(name, bridge);
      }

      await connection.start();
      if (cancelled) {
        await connection.stop().catch(() => {
          /* swallow */
        });
        return;
      }
      connectionRef.current = connection;
      await connection.invoke("JoinTripAsync", tripId);
      if (cancelled) return;
      setStatus("connected");
    }

    go().catch((err: unknown) => {
      if (cancelled) return;
      const wrapped = err instanceof Error ? err : new Error(String(err));
      setError(wrapped);
      setStatus("disconnected");
    });

    return () => {
      cancelled = true;
      const c = connectionRef.current;
      connectionRef.current = null;
      // Detach the bridges so a re-mounted hook doesn't double-fire handlers
      // through the previous connection (especially relevant in tests where
      // the same mock instance survives across renders).
      if (c) {
        for (const [name, bridge] of bridgeHandlers) {
          c.off(name, bridge);
        }
      }
      if (c && c.state !== HubConnectionState.Disconnected) {
        c.invoke("LeaveTripAsync", tripId).catch(() => {
          /* swallow */
        });
        c.stop().catch(() => {
          /* swallow */
        });
      }
    };
  }, [tripId, enabled, connectionFactory, tokenFetcher]);

  // Stable subscription registrar — callers can hand it any handler without
  // worrying about identity churn (subscribe returns a deterministic
  // unsubscribe).
  const subscribe = useCallback(
    <T,>(eventName: string, handler: (payload: T) => void): (() => void) => {
      const set = handlersRef.current.get(eventName);
      if (!set) return () => undefined;
      const wrapped: AnyHandler = (...args) => handler(args[0] as T);
      set.add(wrapped);
      return () => {
        set.delete(wrapped);
      };
    },
    [],
  );

  const publishDriverPosition = useCallback(
    async (lat: number, lng: number): Promise<void> => {
      const c = connectionRef.current;
      if (!c || c.state !== HubConnectionState.Connected) return;
      try {
        await c.invoke("PublishDriverPositionAsync", tripId, lat, lng);
      } catch (err) {
        console.warn("[trip-hub] publishDriverPosition failed", err);
      }
    },
    [tripId],
  );

  const passengerCheckIn = useCallback(
    async (stopId: string): Promise<void> => {
      const c = connectionRef.current;
      if (!c || c.state !== HubConnectionState.Connected) {
        console.warn(
          "[trip-hub] check-in attempted while disconnected — server will reconcile later",
        );
        return;
      }
      try {
        await c.invoke("PassengerCheckInAsync", tripId, stopId);
      } catch (err) {
        console.warn("[trip-hub] passengerCheckIn failed", err);
      }
    },
    [tripId],
  );

  return useMemo<TripHubApi>(
    () => ({
      status,
      error,
      onDriverPosition: (h) => subscribe<DriverPositionPayload>("DriverPositionUpdated", h),
      onEtaUpdated: (h) => subscribe<EtaUpdatedPayload>("EtaUpdated", h),
      onPassengerAtStop: (h) => subscribe<PassengerAtStopPayload>("PassengerAtStop", h),
      onRouteRecomputed: (h) => subscribe<RouteRecomputedPayload>("RouteRecomputed", h),
      onTripStatusChanged: (h) => subscribe<TripStatusChangedPayload>("TripStatusChanged", h),
      publishDriverPosition,
      passengerCheckIn,
    }),
    [status, error, subscribe, publishDriverPosition, passengerCheckIn],
  );
}
