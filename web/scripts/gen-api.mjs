#!/usr/bin/env node
// Generates src/lib/api/types.ts from ../src/Trips.Api/openapi.json (WS4).
// Graceful no-op if the OpenAPI spec hasn't landed yet.
import { existsSync, mkdirSync } from "node:fs";
import { resolve, dirname } from "node:path";
import { fileURLToPath } from "node:url";
import { spawnSync } from "node:child_process";

const __dirname = dirname(fileURLToPath(import.meta.url));
const webRoot = resolve(__dirname, "..");
const openapiPath = resolve(webRoot, "..", "src", "Trips.Api", "openapi.json");
const outPath = resolve(webRoot, "src", "lib", "api", "types.ts");

if (!existsSync(openapiPath)) {
  console.log(
    `[gen:api] openapi.json not found at ${openapiPath} — run WS4 first. Skipping codegen (non-fatal).`,
  );
  process.exit(0);
}

mkdirSync(dirname(outPath), { recursive: true });
const result = spawnSync(
  "npx",
  ["--yes", "openapi-typescript", openapiPath, "-o", outPath],
  { stdio: "inherit", cwd: webRoot },
);
if (result.status !== 0) {
  console.error("[gen:api] openapi-typescript failed");
  process.exit(result.status ?? 1);
}
console.log(`[gen:api] wrote ${outPath}`);
