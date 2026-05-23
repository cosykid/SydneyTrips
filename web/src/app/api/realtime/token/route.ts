// SignalR access-token endpoint.
//
// SignalR negotiates an HTTP handshake then upgrades to a WebSocket. Next.js
// route handlers don't (and shouldn't) proxy raw WebSockets, so the client
// connects directly to the API's hub URL — but it still needs the JWT to
// authenticate. The browser never has the JWT (it lives in the httpOnly
// session cookie), so this endpoint reads the session server-side and hands
// the JWT back to the same-origin client over JSON.
//
// We surface the upstream hub URL as well so the frontend doesn't need to
// know about env vars (and we can swap dev/prod without rebuilding the JS).

import { NextResponse } from "next/server";
import { getServerSession } from "@/lib/auth/server";

interface TokenResponse {
  /** Absolute URL of the SignalR hub on the upstream API. */
  hubUrl: string;
  /** Short-lived API JWT — refreshed alongside the session cookie. */
  accessToken: string;
  /** ISO-8601 expiry; client can refresh before this. */
  expiresAt: string;
}

interface ErrorResponse {
  message: string;
}

function publicApiBase(): string | null {
  // Prefer the public base URL so the browser hits the API directly. Falls
  // back to the server-only base URL when running everything on the same host
  // (e.g. local dev).
  return (
    process.env.NEXT_PUBLIC_API_BASE_URL ??
    process.env.API_BASE_URL ??
    null
  );
}

export async function GET(): Promise<NextResponse<TokenResponse | ErrorResponse>> {
  const session = await getServerSession();
  if (!session?.apiJwt) {
    return NextResponse.json({ message: "Not authenticated" }, { status: 401 });
  }

  const base = publicApiBase();
  if (!base) {
    return NextResponse.json(
      {
        message:
          "API base URL not configured. Set NEXT_PUBLIC_API_BASE_URL so the SignalR client can dial the hub directly.",
      },
      { status: 502 },
    );
  }

  const hubUrl = `${base.replace(/\/$/, "")}/hubs/trip`;
  return NextResponse.json(
    {
      hubUrl,
      accessToken: session.apiJwt,
      expiresAt: session.expiresAt,
    },
    {
      // Don't let intermediaries cache this — it's per-user, short-lived.
      headers: { "Cache-Control": "private, no-store" },
    },
  );
}
