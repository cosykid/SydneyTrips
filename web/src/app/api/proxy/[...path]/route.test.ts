import { afterEach, beforeEach, describe, expect, it, vi } from "vitest";
import { NextRequest } from "next/server";

// The proxy reads UPSTREAM_BASE at module load, so stub the env *before*
// importing the route handlers.
async function loadRoute() {
  vi.stubEnv("API_BASE_URL", "http://upstream.test");
  return import("./route");
}

function ctx(path: string[]) {
  return { params: Promise.resolve({ path }) };
}

describe("proxy route — null-body statuses", () => {
  beforeEach(() => {
    vi.resetModules();
  });
  afterEach(() => {
    vi.unstubAllEnvs();
    vi.restoreAllMocks();
  });

  it("forwards a 204 DELETE without throwing on the empty body", async () => {
    // Upstream replies 204 No Content — a null-body status. Reconstructing it
    // with the drained (empty) ArrayBuffer used to throw a TypeError, surfacing
    // to the browser as a 500 and breaking participant removal.
    vi.stubGlobal(
      "fetch",
      vi.fn(async () => new Response(null, { status: 204 })),
    );

    const { DELETE } = await loadRoute();
    const req = new NextRequest(
      "http://localhost/api/proxy/trips/t-1/participants/p-1",
      { method: "DELETE" },
    );

    const res = await DELETE(req, ctx(["trips", "t-1", "participants", "p-1"]));

    expect(res.status).toBe(204);
    expect(await res.text()).toBe("");
  });

  it("still passes JSON bodies through for normal 200 responses", async () => {
    vi.stubGlobal(
      "fetch",
      vi.fn(
        async () =>
          new Response(JSON.stringify({ ok: true }), {
            status: 200,
            headers: { "content-type": "application/json" },
          }),
      ),
    );

    const { GET } = await loadRoute();
    const req = new NextRequest("http://localhost/api/proxy/trips/t-1", {
      method: "GET",
    });

    const res = await GET(req, ctx(["trips", "t-1"]));

    expect(res.status).toBe(200);
    expect(await res.json()).toEqual({ ok: true });
  });
});
