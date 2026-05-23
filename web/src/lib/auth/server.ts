import { cookies } from "next/headers";
import { SESSION_COOKIE, unsealSession, type SessionPayload } from "./session";

export async function getServerSession(): Promise<SessionPayload | null> {
  const jar = await cookies();
  const raw = jar.get(SESSION_COOKIE)?.value;
  if (!raw) return null;
  return unsealSession(raw);
}
