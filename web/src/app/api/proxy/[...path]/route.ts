// Catch-all API proxy. The browser only ever talks to /api/proxy/...;
// we forward to the upstream Trips API with the request's cookies attached so
// the anonymous `trips_session` cookie issued by the API flows on every call
// (and any Set-Cookie that comes back is mirrored to the browser).
//
// No JWT, no auth gate. The API itself decides what the cookie grants.

import { NextResponse, type NextRequest } from "next/server";

const UPSTREAM_BASE =
  process.env.API_BASE_URL ?? process.env.NEXT_PUBLIC_API_BASE_URL ?? "";

// Hop-by-hop headers should never be forwarded — they're scoped to the
// transport, not the message. We also strip `host` so fetch picks the upstream
// host from the URL rather than echoing the Next.js host.
// Statuses that MUST NOT carry a body (Fetch spec "null body status"). The
// Response constructor throws a TypeError if handed any body — even a
// zero-length ArrayBuffer — alongside one of these. The DELETE endpoints reply
// 204, so reconstructing the upstream response naively crashed this proxy.
const NULL_BODY_STATUS = new Set([101, 103, 204, 205, 304]);

const HOP_BY_HOP = new Set([
  "connection",
  "content-length",
  "host",
  "keep-alive",
  "proxy-authenticate",
  "proxy-authorization",
  "te",
  "trailers",
  "transfer-encoding",
  "upgrade",
]);

async function forward(request: NextRequest, path: string[]): Promise<NextResponse> {
  if (!UPSTREAM_BASE) {
    return NextResponse.json(
      { message: "API base URL not configured (set API_BASE_URL or NEXT_PUBLIC_API_BASE_URL)" },
      { status: 502 },
    );
  }

  const search = request.nextUrl.search ?? "";
  const target = `${UPSTREAM_BASE}/${path.join("/")}${search}`;

  const headers = new Headers();
  for (const [key, value] of request.headers.entries()) {
    if (!HOP_BY_HOP.has(key.toLowerCase())) headers.set(key, value);
  }
  // Cookie header from the browser flows straight through — that's how the
  // upstream API sees the anonymous-session cookie it issued earlier. We do NOT
  // strip it (the old auth-by-JWT path did).

  const init: RequestInit = {
    method: request.method,
    headers,
    redirect: "manual",
  };

  if (!["GET", "HEAD"].includes(request.method)) {
    init.body = await request.arrayBuffer();
  }

  const upstream = await fetch(target, init);
  const responseHeaders = new Headers();
  for (const [key, value] of upstream.headers.entries()) {
    if (!HOP_BY_HOP.has(key.toLowerCase())) responseHeaders.set(key, value);
  }
  // Headers.entries collapses duplicate Set-Cookie headers via getSetCookie.
  // Re-fetch them explicitly so a multi-cookie response survives the proxy.
  responseHeaders.delete("set-cookie");
  const setCookies = upstream.headers.getSetCookie?.() ?? [];
  for (const sc of setCookies) {
    responseHeaders.append("set-cookie", sc);
  }

  // A null-body status must be returned with no body or the NextResponse
  // constructor throws. Still drain the upstream stream to release the socket.
  const buffer = await upstream.arrayBuffer();
  const body = NULL_BODY_STATUS.has(upstream.status) ? null : buffer;
  return new NextResponse(body, {
    status: upstream.status,
    headers: responseHeaders,
  });
}

interface RouteContext {
  params: Promise<{ path: string[] }>;
}

export async function GET(request: NextRequest, ctx: RouteContext): Promise<NextResponse> {
  return forward(request, (await ctx.params).path);
}
export async function POST(request: NextRequest, ctx: RouteContext): Promise<NextResponse> {
  return forward(request, (await ctx.params).path);
}
export async function PUT(request: NextRequest, ctx: RouteContext): Promise<NextResponse> {
  return forward(request, (await ctx.params).path);
}
export async function PATCH(request: NextRequest, ctx: RouteContext): Promise<NextResponse> {
  return forward(request, (await ctx.params).path);
}
export async function DELETE(request: NextRequest, ctx: RouteContext): Promise<NextResponse> {
  return forward(request, (await ctx.params).path);
}
