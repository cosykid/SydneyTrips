// Session cookie helpers — wraps the API JWT in a signed cookie so it never
// reaches client JavaScript. We re-sign with our own AUTH_SECRET so the cookie
// is opaque to the API; the original JWT is the payload.
//
// This is intentionally simple: the API JWT remains the source of truth for
// scopes/expiry. We just need a tamper-resistant transport that supports
// httpOnly. NextAuth is installed but not adopted here because the API already
// owns user identity — we just need a session shuttle.

import { SignJWT, jwtVerify } from "jose";

export const SESSION_COOKIE = process.env.SESSION_COOKIE_NAME ?? "trips_session";
const ALGORITHM = "HS256";

interface SessionPayload {
  // The API-issued JWT we forward on every proxied call.
  apiJwt: string;
  // Mirrored user fields for cheap server-side reads.
  userId: string;
  email: string;
  displayName: string;
  // ISO-8601 expiry from the API token; used to set the cookie's maxAge.
  expiresAt: string;
}

function getSecret(): Uint8Array {
  const secret = process.env.AUTH_SECRET;
  if (!secret || secret.length < 16) {
    throw new Error(
      "AUTH_SECRET env var is required (32+ chars). See web/.env.example.",
    );
  }
  return new TextEncoder().encode(secret);
}

export async function sealSession(payload: SessionPayload): Promise<string> {
  const expirySeconds = Math.floor(new Date(payload.expiresAt).getTime() / 1000);
  return new SignJWT({ ...payload })
    .setProtectedHeader({ alg: ALGORITHM })
    .setIssuedAt()
    .setExpirationTime(expirySeconds)
    .sign(getSecret());
}

export async function unsealSession(cookie: string): Promise<SessionPayload | null> {
  try {
    const { payload } = await jwtVerify(cookie, getSecret(), {
      algorithms: [ALGORITHM],
    });
    return payload as unknown as SessionPayload;
  } catch {
    return null;
  }
}

export type { SessionPayload };
