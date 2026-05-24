// API DTOs use a flat lon/lat + hasCar/seats shape; the UI prefers a nested
// LatLng + "driver"/"passenger" role. This file is the single place where the
// translation happens, so components can stay framework-agnostic about the
// wire format.

import type { components } from "./types";
import type {
  CandidateNode,
  CostSplit,
  LatLng,
  Participant,
  ParticipantRole,
  Solution,
  SolutionRoute,
  SolutionStop,
  Trip,
  TripStatus,
  TripSummary,
} from "./schema";
import { driverColour } from "@/lib/map/palette";

type TripDto = components["schemas"]["TripDto"];
type TripDetailDto = components["schemas"]["TripDetailDto"];
type ParticipantWithNodesDto = components["schemas"]["ParticipantWithNodesDto"];
type ParticipantDto = components["schemas"]["ParticipantDto"];
type CandidateNodeDto = components["schemas"]["CandidateNodeDto"];
type CostSplitResponse = components["schemas"]["CostSplitResponse"];
type SolutionDto = components["schemas"]["SolutionDto"];
type DriverRouteDto = components["schemas"]["DriverRouteDto"];
type StopDto = components["schemas"]["StopDto"];

// openapi-typescript renders `format: double` as `number | string`; the API
// always returns numbers but we coerce to be safe.
function num(v: number | string): number {
  return typeof v === "number" ? v : Number(v);
}

function pointToLatLng(longitude: number | string, latitude: number | string): LatLng {
  return { lat: num(latitude), lng: num(longitude) };
}

function roleFromHasCar(hasCar: boolean): ParticipantRole {
  return hasCar ? "driver" : "passenger";
}

function modalityFromKind(kind: number): CandidateNode["modality"] {
  // Mirrors Trips.Core.Domain.NodeKind. Keep these ordinals in sync with the
  // enum on the .NET side (CandidateNodeKindDto).
  switch (kind) {
    case 1:
      return "train_station";
    case 2:
      return "bus_stop";
    case 3:
      return "ferry_wharf";
    case 4:
      return "light_rail";
    default:
      return "generic";
  }
}

function arrivalWindowMinutes(earliest: string, latest: string): number {
  const ms = new Date(latest).getTime() - new Date(earliest).getTime();
  // The UI shows "+/- N min", which is the half-width of the window.
  return Math.max(0, Math.round(ms / 60_000 / 2));
}

/**
 * The backend's `departAt` is the scheduled trip start, set ~1 hour before
 * the user's intended arrival. UI shows "Arrive by …", so we derive that
 * from the midpoint of the arrival window and surface it as `arriveBy` on
 * the FE Trip/TripSummary shape.
 */
function arriveByFromWindow(earliest: string, latest: string): string {
  const mid = (new Date(earliest).getTime() + new Date(latest).getTime()) / 2;
  return new Date(mid).toISOString();
}

function statusFromTrip(lockedSolutionId: string | null, referenceTime: string): TripStatus {
  if (!lockedSolutionId) return "draft";
  const ref = new Date(referenceTime).getTime();
  const now = Date.now();
  if (now < ref) return "planned";
  return "in_progress";
}

export function apiToTripSummary(t: TripDto): TripSummary {
  return {
    id: t.id,
    name: t.name,
    destinationAddress: t.destinationName,
    destination: pointToLatLng(t.destinationLongitude, t.destinationLatitude),
    arriveBy: arriveByFromWindow(t.arrivalWindowEarliest, t.arrivalWindowLatest),
    arrivalWindowMinutes: arrivalWindowMinutes(t.arrivalWindowEarliest, t.arrivalWindowLatest),
    status: statusFromTrip(t.lockedSolutionId, t.departAt),
    participantCount: num(t.participantCount),
    hasLockedSolution: Boolean(t.lockedSolutionId),
  };
}

export function apiParticipantToUi(p: ParticipantWithNodesDto | ParticipantDto): Participant {
  // ParticipantDto and ParticipantWithNodesDto share the participant fields;
  // candidate nodes only appear on the detail variant.
  return {
    id: p.id,
    displayName: p.displayName,
    role: roleFromHasCar(p.hasCar),
    originAddress: "",
    origin: pointToLatLng(p.homeLongitude, p.homeLatitude),
    seatsAvailable: p.hasCar ? num(p.seats) : undefined,
    prefs: {
      // The .NET side stores walk budget in minutes — we approximate ~80 m/min
      // walking pace so the UI can keep its metres semantics.
      maxWalkMetres: num(p.preferences.walkBudgetMins) * 80,
      maxDetourMinutes: num(p.preferences.detourToleranceMins),
    },
  };
}

export function apiCandidateNodeToUi(n: CandidateNodeDto): CandidateNode {
  return {
    id: n.id,
    participantId: n.participantId,
    location: pointToLatLng(n.longitude, n.latitude),
    label: n.displayName ?? n.externalId ?? undefined,
    modality: modalityFromKind(num(n.kind)),
    walkMins: num(n.walkMins),
    ptMins: num(n.ptMins),
    path: n.path?.coordinates?.map((c) => pointToLatLng(c.longitude, c.latitude)),
  };
}

export function apiToTrip(t: TripDetailDto): Trip {
  const participants = t.participants.map(apiParticipantToUi);
  const candidateNodes = t.participants.flatMap((p) =>
    p.candidateNodes.map(apiCandidateNodeToUi),
  );
  return {
    id: t.id,
    name: t.name,
    destinationAddress: t.destinationName,
    destination: pointToLatLng(t.destinationLongitude, t.destinationLatitude),
    arriveBy: arriveByFromWindow(t.arrivalWindowEarliest, t.arrivalWindowLatest),
    arrivalWindowMinutes: arrivalWindowMinutes(t.arrivalWindowEarliest, t.arrivalWindowLatest),
    status: statusFromTrip(t.lockedSolutionId, t.departAt),
    participantCount: participants.length,
    hasLockedSolution: Boolean(t.lockedSolutionId),
    participants,
    candidateNodes,
    lockedSolutionId: t.lockedSolutionId ?? undefined,
  };
}

export function apiToCostSplit(r: CostSplitResponse): CostSplit {
  return {
    tripId: r.tripId,
    currency: "AUD",
    totalCost: num(r.totalCost),
    perParticipant: r.entries.map((e) => ({
      participantId: e.participantId,
      displayName: e.displayName,
      amount: num(e.total),
      breakdown: { fuel: num(e.fuelShare), tolls: num(e.tollShare) },
    })),
  };
}

function apiStopToUi(s: StopDto): SolutionStop {
  const legs = s.pickups.map((p) => ({
    participantId: p.participantId,
    walkMins: num(p.walkMins),
    ptMins: num(p.ptMins),
  }));
  // walkMetres is the legacy aggregate field — approximate by summing each leg's walk minutes
  // at ~80 m/min so existing consumers (CostBreakdown, etc.) keep working.
  const walkMetres = legs.reduce((m, l) => m + l.walkMins * 80, 0);
  return {
    candidateNodeId: s.candidateNodeId,
    location: pointToLatLng(s.longitude, s.latitude),
    arriveAt: s.estimatedArrival,
    nodeKind: modalityFromKind(num(s.nodeKind)),
    pickupLegs: legs,
    passengerIds: legs.map((l) => l.participantId),
    walkMetres,
  };
}

function apiRouteToUi(r: DriverRouteDto, idx: number, trip: Trip): SolutionRoute {
  const driver = trip.participants.find((p) => p.id === r.driverId);
  const stops = r.stops.map(apiStopToUi);
  // The API doesn't store a snapped polyline. Approximate it by chaining
  // driver origin → stops → destination so the LiveMap can still render a line.
  const polyline: LatLng[] = [];
  if (driver) polyline.push(driver.origin);
  for (const s of stops) polyline.push(s.location);
  polyline.push(trip.destination);
  return {
    driverParticipantId: r.driverId,
    driverDisplayName: driver?.displayName ?? "Driver",
    colour: driverColour(idx),
    polyline,
    stops,
    drivingMinutes: num(r.travelMins),
    drivingDistanceKm: 0,
  };
}

export function apiToSolution(s: SolutionDto, trip: Trip): Solution {
  const routes = s.routes.map((r, idx) => apiRouteToUi(r, idx, trip));
  const totalDrivingMinutes = routes.reduce((sum, r) => sum + r.drivingMinutes, 0);
  const maxDrivingMinutes = routes.reduce((m, r) => Math.max(m, r.drivingMinutes), 0);
  const totalStops = routes.reduce((sum, r) => sum + r.stops.length, 0);
  return {
    id: s.id,
    label: s.label,
    metrics: {
      totalDrivingMinutes,
      maxDrivingMinutes,
      totalStops,
      totalWalkMetres: 0,
      maxWalkMetres: 0,
      fairnessIndex: 0,
    },
    routes,
  };
}
