import { defineConfig, devices } from "@playwright/test";

// Playwright runs three suites with different sensitivities to the backend:
//
//   tests/e2e/smoke.spec.ts          — UI-only smoke; runs against the Next.js dev server alone
//   tests/e2e/{single,multi,what-if} — full-stack E2E; requires a running Trips.Api (docker
//                                       compose + dotnet ef database update + dotnet run)
//   tests/screenshots.spec.ts        — produces docs/screenshots/*.png from a seeded trip;
//                                       expects tests/seed/seed-demo.sh to have written
//                                       /tmp/seed-demo.json
//
// The webServer entry boots Next.js automatically. If you also need the API, run
// `docker compose up -d` + `dotnet run --project src/Trips.Api` in a separate terminal.
export default defineConfig({
  testDir: "./tests",
  testIgnore: ["**/*.test.ts", "**/*.test.tsx"],
  fullyParallel: false, // E2E specs seed shared API state — keep them serial-safe.
  forbidOnly: !!process.env.CI,
  retries: process.env.CI ? 1 : 0,
  reporter: [["list"]],
  use: {
    baseURL: "http://localhost:3000",
    trace: "on-first-retry",
    screenshot: "only-on-failure",
  },
  webServer: {
    command: "npm run dev",
    url: "http://localhost:3000",
    reuseExistingServer: !process.env.CI,
    timeout: 120_000,
  },
  projects: [
    {
      name: "chromium",
      use: { ...devices["Desktop Chrome"] },
    },
  ],
});
