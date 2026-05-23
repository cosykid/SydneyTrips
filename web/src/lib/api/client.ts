// Thin fetch wrapper for the Trips API.
//
// Every browser call goes through the Next.js route-handler proxy at
// /api/proxy/* so the anonymous `trips_session` cookie stays same-origin (the
// API issues the cookie, the proxy mirrors Set-Cookie back, and subsequent
// requests carry it). No JWT, no localStorage token — the cookie is the
// session, and on a 401 we just bubble the error (no redirect because there's
// no login page to redirect to).

export class ApiError extends Error {
  status: number;
  payload: unknown;

  constructor(status: number, message: string, payload: unknown) {
    super(message);
    this.name = "ApiError";
    this.status = status;
    this.payload = payload;
  }
}

export interface ApiRequestInit extends Omit<RequestInit, "body" | "headers"> {
  body?: unknown;
  headers?: Record<string, string>;
  /** Override the base URL (server-only paths). Defaults to the proxy. */
  baseUrl?: string;
  /** Explicit bearer token. Only used by callers that genuinely need one (none
   * in this app since auth was removed) — kept for backwards-compat with any
   * lingering server-side flows. */
  bearerToken?: string;
  /** No-op kept for backwards-compat with callers — auth has been removed and
   * 401 is just an error now. */
  ignoreUnauthorized?: boolean;
}

function getDefaultBaseUrl(): string {
  // Browser side: hit the same-origin proxy so the trips_session cookie flows.
  if (typeof window !== "undefined") return "/api/proxy";
  // Server side fallback (mostly used by the proxy handler itself).
  return process.env.API_BASE_URL ?? process.env.NEXT_PUBLIC_API_BASE_URL ?? "";
}

export async function apiFetch<T>(path: string, init: ApiRequestInit = {}): Promise<T> {
  // `ignoreUnauthorized` is accepted for backwards-compat but no longer
  // changes behaviour (auth was removed).
  const { body, headers, baseUrl, bearerToken, ignoreUnauthorized, ...rest } = init;
  void ignoreUnauthorized;
  const url = `${baseUrl ?? getDefaultBaseUrl()}${path}`;

  const finalHeaders: Record<string, string> = {
    Accept: "application/json",
    ...(body !== undefined ? { "Content-Type": "application/json" } : {}),
    ...(bearerToken ? { Authorization: `Bearer ${bearerToken}` } : {}),
    ...headers,
  };

  const response = await fetch(url, {
    ...rest,
    headers: finalHeaders,
    body: body !== undefined ? JSON.stringify(body) : undefined,
    credentials: "include",
  });

  const contentType = response.headers.get("content-type") ?? "";
  const isJson = contentType.includes("application/json");
  const payload: unknown = isJson ? await response.json().catch(() => null) : await response.text();

  if (!response.ok) {
    const message =
      isJson && payload && typeof payload === "object" && "message" in payload
        ? String((payload as { message: unknown }).message)
        : `Request failed: ${response.status}`;
    throw new ApiError(response.status, message, payload);
  }

  return payload as T;
}
