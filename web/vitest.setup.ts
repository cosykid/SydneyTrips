import "@testing-library/jest-dom/vitest";
import { afterEach, vi } from "vitest";
import { cleanup } from "@testing-library/react";

afterEach(() => {
  cleanup();
});

// Mapbox-gl uses Worker and other browser-only APIs that jsdom doesn't ship.
// Tests for map components mock the react-map-gl module rather than relying
// on the real GL renderer.
vi.mock("mapbox-gl/dist/mapbox-gl.css", () => ({}));
