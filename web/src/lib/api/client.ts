// Thin fetch wrapper for the Trips API.
//
// The browser never sees the JWT directly — every API call goes through the
// Next.js route-handler proxy at /api/proxy/* which reads the httpOnly session
// cookie server-side and forwards the bearer token. This avoids storing the
// JWT in localStorage and lets `proxy.ts` enforce auth on /trips/*.
//
// In tests or non-browser contexts (e.g. server components) callers can pass
// an explicit `bearerToken` and a target `baseUrl` to skip the proxy.

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
  /** Explicit bearer token. Only used when bypassing the proxy. */
  bearerToken?: string;
  /** Treat 401 as a regular error instead of triggering a redirect. */
  ignoreUnauthorized?: boolean;
}

function getDefaultBaseUrl(): string {
  // Browser side: hit the same-origin proxy that injects the JWT.
  if (typeof window !== "undefined") return "/api/proxy";
  // Server side fallback (mostly used by the proxy handler itself).
  return process.env.API_BASE_URL ?? process.env.NEXT_PUBLIC_API_BASE_URL ?? "";
}

export async function apiFetch<T>(path: string, init: ApiRequestInit = {}): Promise<T> {
  const { body, headers, baseUrl, bearerToken, ignoreUnauthorized, ...rest } = init;
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

  if (response.status === 401 && !ignoreUnauthorized && typeof window !== "undefined") {
    // Boot back to /login. Route handler proxies also drop the session cookie
    // when the upstream rejects.
    const next = encodeURIComponent(window.location.pathname + window.location.search);
    window.location.href = `/login?next=${next}`;
    throw new ApiError(401, "Unauthorized", null);
  }

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
