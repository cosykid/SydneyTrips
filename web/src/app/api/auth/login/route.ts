import { NextResponse, type NextRequest } from "next/server";
import { apiFetch } from "@/lib/api/client";
import type { AuthLoginRequest, AuthResponse } from "@/lib/api/schema";
import { SESSION_COOKIE, sealSession } from "@/lib/auth/session";

export async function POST(request: NextRequest): Promise<NextResponse> {
  const body = (await request.json()) as AuthLoginRequest;
  try {
    const auth = await apiFetch<AuthResponse>("/auth/login", {
      method: "POST",
      body,
      baseUrl: process.env.API_BASE_URL ?? process.env.NEXT_PUBLIC_API_BASE_URL ?? "",
      ignoreUnauthorized: true,
    });
    const sealed = await sealSession({
      apiJwt: auth.token,
      userId: auth.user.id,
      email: auth.user.email,
      displayName: auth.user.displayName,
      expiresAt: auth.expiresAt,
    });
    const res = NextResponse.json({ user: auth.user });
    const maxAge = Math.max(0, Math.floor((new Date(auth.expiresAt).getTime() - Date.now()) / 1000));
    res.cookies.set(SESSION_COOKIE, sealed, {
      httpOnly: true,
      sameSite: "lax",
      secure: process.env.NODE_ENV === "production",
      maxAge,
      path: "/",
    });
    return res;
  } catch (error) {
    const message = error instanceof Error ? error.message : "Login failed";
    const status =
      error && typeof error === "object" && "status" in error
        ? (error as { status: number }).status
        : 502;
    return NextResponse.json({ message }, { status });
  }
}
