// Catch-all API proxy: forwards the request to the upstream Trips API and
// injects the API JWT pulled from the httpOnly session cookie. The browser
// only ever sees /api/proxy/... — the real API base URL stays server-side.
//
// On 401 from the upstream we clear the session cookie so the next nav
// triggers the optimistic redirect in proxy.ts.

import { NextResponse, type NextRequest } from "next/server";
import { SESSION_COOKIE } from "@/lib/auth/session";
import { getServerSession } from "@/lib/auth/server";

const UPSTREAM_BASE =
  process.env.API_BASE_URL ?? process.env.NEXT_PUBLIC_API_BASE_URL ?? "";

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

  const session = await getServerSession();
  const search = request.nextUrl.search ?? "";
  const target = `${UPSTREAM_BASE}/${path.join("/")}${search}`;

  const headers = new Headers();
  for (const [key, value] of request.headers.entries()) {
    if (!HOP_BY_HOP.has(key.toLowerCase())) headers.set(key, value);
  }
  headers.delete("cookie");
  if (session?.apiJwt) headers.set("authorization", `Bearer ${session.apiJwt}`);

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
  const buffer = await upstream.arrayBuffer();
  const res = new NextResponse(buffer, {
    status: upstream.status,
    headers: responseHeaders,
  });

  if (upstream.status === 401) {
    res.cookies.set(SESSION_COOKIE, "", {
      httpOnly: true,
      sameSite: "lax",
      secure: process.env.NODE_ENV === "production",
      maxAge: 0,
      path: "/",
    });
  }
  return res;
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
