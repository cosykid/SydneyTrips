"use client";

import { useMemo } from "react";
import MapboxMap, { Layer, Source, type MapRef } from "react-map-gl/mapbox";
import "mapbox-gl/dist/mapbox-gl.css";
import type {
  CandidateNode,
  LatLng,
  Participant,
  Solution,
  TripSummary,
} from "@/lib/api/schema";
import { driverColour } from "@/lib/map/palette";
import type { MapViewState } from "@/lib/store";

export interface PlanMapProps {
  destination: { address: string; point: LatLng };
  participants: Participant[];
  candidateNodes: CandidateNode[];
  solution?: Solution;
  trip?: Pick<TripSummary, "id" | "name">;
  viewState: MapViewState;
  onMove: (next: MapViewState) => void;
  mapRef?: React.Ref<MapRef>;
}

interface PointFeature {
  type: "Feature";
  geometry: { type: "Point"; coordinates: [number, number] };
  properties: Record<string, string | number | boolean>;
}

interface LineFeature {
  type: "Feature";
  geometry: { type: "LineString"; coordinates: Array<[number, number]> };
  properties: Record<string, string | number | boolean>;
}

interface FeatureCollection<F> {
  type: "FeatureCollection";
  features: F[];
}

function point(loc: LatLng, props: Record<string, string | number | boolean>): PointFeature {
  return {
    type: "Feature",
    geometry: { type: "Point", coordinates: [loc.lng, loc.lat] },
    properties: props,
  };
}

export function PlanMap({
  destination,
  participants,
  candidateNodes,
  solution,
  viewState,
  onMove,
  mapRef,
}: PlanMapProps): React.JSX.Element {
  const token = process.env.NEXT_PUBLIC_MAPBOX_TOKEN;

  const origins = useMemo<FeatureCollection<PointFeature>>(
    () => ({
      type: "FeatureCollection",
      features: participants.map((p) =>
        point(p.origin, {
          id: p.id,
          name: p.displayName,
          role: p.role,
          radius: p.role === "driver" ? 8 : 5,
        }),
      ),
    }),
    [participants],
  );

  const candidates = useMemo<FeatureCollection<PointFeature>>(
    () => ({
      type: "FeatureCollection",
      features: candidateNodes.map((c) =>
        point(c.location, { id: c.id, label: c.label ?? "", modality: c.modality ?? "generic" }),
      ),
    }),
    [candidateNodes],
  );

  const chosenNodes = useMemo<FeatureCollection<PointFeature>>(() => {
    if (!solution) return { type: "FeatureCollection", features: [] };
    return {
      type: "FeatureCollection",
      features: solution.routes.flatMap((route) =>
        route.stops
          .filter((s) => s.candidateNodeId)
          .map((s) =>
            point(s.location, {
              driverId: route.driverParticipantId,
              colour: route.colour,
              passengers: s.passengerIds.length,
            }),
          ),
      ),
    };
  }, [solution]);

  const routes = useMemo<FeatureCollection<LineFeature>>(() => {
    if (!solution) return { type: "FeatureCollection", features: [] };
    return {
      type: "FeatureCollection",
      features: solution.routes.map((route, idx) => ({
        type: "Feature",
        geometry: {
          type: "LineString",
          coordinates: route.polyline.map((p) => [p.lng, p.lat] as [number, number]),
        },
        properties: {
          driverId: route.driverParticipantId,
          driverName: route.driverDisplayName,
          colour: route.colour ?? driverColour(idx),
        },
      })),
    };
  }, [solution]);

  const destinationFc = useMemo<FeatureCollection<PointFeature>>(
    () => ({
      type: "FeatureCollection",
      features: [point(destination.point, { label: destination.address })],
    }),
    [destination.address, destination.point],
  );

  if (!token) {
    return (
      <div
        className="bg-muted text-muted-foreground flex h-full w-full items-center justify-center p-6 text-center text-sm"
        data-testid="map-missing-token"
      >
        <div>
          <p className="font-medium">Map disabled</p>
          <p>
            Set <code className="bg-background rounded px-1">NEXT_PUBLIC_MAPBOX_TOKEN</code> in
            web/.env.local to enable the Mapbox canvas.
          </p>
        </div>
      </div>
    );
  }

  return (
    <MapboxMap
      ref={mapRef}
      mapboxAccessToken={token}
      mapStyle="mapbox://styles/mapbox/light-v11"
      latitude={viewState.latitude}
      longitude={viewState.longitude}
      zoom={viewState.zoom}
      onMove={(evt) =>
        onMove({
          latitude: evt.viewState.latitude,
          longitude: evt.viewState.longitude,
          zoom: evt.viewState.zoom,
        })
      }
      style={{ width: "100%", height: "100%" }}
    >
      {/* Candidate PT nodes — small grey */}
      <Source id="candidate-nodes" type="geojson" data={candidates}>
        <Layer
          id="candidate-nodes-layer"
          type="circle"
          paint={{
            "circle-radius": 3,
            "circle-color": "#9CA3AF",
            "circle-opacity": 0.7,
            "circle-stroke-width": 0.5,
            "circle-stroke-color": "#4B5563",
          }}
        />
      </Source>

      {/* Driver routes — categorical lines */}
      <Source id="driver-routes" type="geojson" data={routes}>
        <Layer
          id="driver-routes-layer"
          type="line"
          paint={{
            "line-color": ["get", "colour"],
            "line-width": 4,
            "line-opacity": 0.8,
          }}
          layout={{ "line-cap": "round", "line-join": "round" }}
        />
      </Source>

      {/* Chosen pickup nodes — green */}
      <Source id="chosen-nodes" type="geojson" data={chosenNodes}>
        <Layer
          id="chosen-nodes-layer"
          type="circle"
          paint={{
            "circle-radius": 7,
            "circle-color": "#10B981",
            "circle-stroke-width": 2,
            "circle-stroke-color": "#065F46",
          }}
        />
      </Source>

      {/* Participant origins — red dots (larger for drivers) */}
      <Source id="origins" type="geojson" data={origins}>
        <Layer
          id="origins-layer"
          type="circle"
          paint={{
            "circle-radius": ["get", "radius"],
            "circle-color": "#DC2626",
            "circle-stroke-width": 1.5,
            "circle-stroke-color": "#7F1D1D",
          }}
        />
      </Source>

      {/* Destination — star */}
      <Source id="destination" type="geojson" data={destinationFc}>
        <Layer
          id="destination-symbol"
          type="symbol"
          layout={{
            "text-field": "★",
            "text-size": 28,
            "text-allow-overlap": true,
            "text-offset": [0, -0.1],
          }}
          paint={{
            "text-color": "#111827",
            "text-halo-color": "#FBBF24",
            "text-halo-width": 1.6,
          }}
        />
      </Source>
    </MapboxMap>
  );
}
