import type { NextConfig } from "next";
import * as path from "node:path";

const nextConfig: NextConfig = {
  // Pin Turbopack to this directory so it doesn't wander up looking for a
  // workspace root when multiple lockfiles exist on the dev machine.
  turbopack: {
    root: path.resolve("."),
  },
};

export default nextConfig;
