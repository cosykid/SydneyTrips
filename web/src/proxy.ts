// Next.js 16 renames `middleware.ts` to `proxy.ts`. The function signature and
// behaviour are unchanged: it runs at the edge before the request hits a
// route, so we use it for an *optimistic* auth check — verifying the JWT
// signature happens later inside the API proxy / RSCs, since proxy is
// explicitly not meant for slow data work (see docs/01-app/01-getting-started/16-proxy.md).
import { NextResponse } from "next/server";
import type { NextRequest } from "next/server";
import { SESSION_COOKIE } from "@/lib/auth/session";

export function proxy(request: NextRequest): NextResponse {
  const { pathname } = request.nextUrl;
  const isProtected = pathname.startsWith("/trips");
  if (!isProtected) return NextResponse.next();

  const hasCookie = Boolean(request.cookies.get(SESSION_COOKIE)?.value);
  if (hasCookie) return NextResponse.next();

  const loginUrl = new URL("/login", request.url);
  loginUrl.searchParams.set("next", pathname + (request.nextUrl.search ?? ""));
  return NextResponse.redirect(loginUrl);
}

export const config = {
  matcher: ["/trips/:path*"],
};
