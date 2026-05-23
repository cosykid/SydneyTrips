// Next.js 16's edge-runtime hook (renamed from `middleware.ts`). With auth gone
// there's nothing for us to gate — every browser has an anonymous session
// cookie minted on first API call. We keep the file as a no-op so the matcher
// stays accurate and onlookers know auth is intentionally not enforced here.
import { NextResponse, type NextRequest } from "next/server";

export function proxy(request: NextRequest): NextResponse {
  void request;
  return NextResponse.next();
}

export const config = {
  matcher: ["/trips/:path*"],
};
