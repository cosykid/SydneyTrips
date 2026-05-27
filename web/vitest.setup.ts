import "@testing-library/jest-dom/vitest";
import { afterEach } from "vitest";
import { cleanup } from "@testing-library/react";

afterEach(() => {
  cleanup();
});

// Map components are tested against the SVG MapFallback (no NEXT_PUBLIC_GOOGLE_MAPS_KEY
// in the test env), so the real Google Maps renderer never loads under jsdom.
