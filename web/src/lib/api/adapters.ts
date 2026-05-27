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
type CandidateNodeKindDto = components["schemas"]["CandidateNodeKindDto"];

const WALK_METRES_PER_MINUTE = 80;

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

function modalityFromKind(kind: CandidateNodeKindDto): CandidateNode["modality"] {
  // CandidateNodeKindDto serialises as a camelCase string enum (JsonStringEnumConverter),
  // not an ordinal — match on the wire names. "home" and anything unknown is a plain point.
  switch (kind) {
    case "trainStation":
      return "train_station";
    case "busStop":
      return "bus_stop";
    case "wharf":
      return "ferry_wharf";
    case "lightRailStop":
      return "light_rail";
    default:
      return "generic";
  }
}

/**
 * The backend's `departAt` is the scheduled trip start, set ~1 hour before
 * the user's intended arrival. UI shows "Arrive by …", so we derive that
 * from the midpoint of the arrival window and surface it as `arriveBy` on
 * the FE Trip/TripSummary shape. (Created trips collapse the window to a
 * single instant, so `earliest == latest` and the midpoint is exact.)
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
      maxWalkMetres: num(p.preferences.walkBudgetMins) * WALK_METRES_PER_MINUTE,
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
    modality: modalityFromKind(n.kind),
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
    path: p.path?.coordinates?.map((c) => pointToLatLng(c.longitude, c.latitude)),
    pathLegs: p.pathLegs?.map((leg) => ({
      mode: leg.mode,
      path: leg.path.coordinates.map((c) => pointToLatLng(c.longitude, c.latitude)),
      durationMins: num(leg.durationMins),
      fromName: leg.fromName ?? undefined,
      toName: leg.toName ?? undefined,
      routeShortName: leg.routeShortName ?? undefined,
      departureTime: leg.departureTime ?? undefined,
      arrivalTime: leg.arrivalTime ?? undefined,
    })),
  }));
  // walkMetres is the legacy aggregate field — approximate by summing each leg's walk minutes
  // at ~80 m/min so existing consumers (CostBreakdown, etc.) keep working.
  const walkMetres = legs.reduce((m, l) => m + l.walkMins * WALK_METRES_PER_MINUTE, 0);
  return {
    candidateNodeId: s.candidateNodeId,
    location: pointToLatLng(s.longitude, s.latitude),
    arriveAt: s.estimatedArrival,
    nodeKind: modalityFromKind(s.nodeKind),
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
    destinationArrival: r.destinationArrival ?? undefined,
    departure: r.departure ?? undefined,
  };
}

function minutesBetween(start?: string, end?: string): number | null {
  if (!start || !end) return null;
  const startMs = new Date(start).getTime();
  const endMs = new Date(end).getTime();
  if (Number.isNaN(startMs) || Number.isNaN(endMs)) return null;
  return Math.max(0, (endMs - startMs) / 60000);
}

export function apiToSolution(s: SolutionDto, trip: Trip): Solution {
  const routes = s.routes.map((r, idx) => apiRouteToUi(r, idx, trip));
  const totalDrivingMinutes = routes.reduce((sum, r) => sum + r.drivingMinutes, 0);
  const maxDrivingMinutes = routes.reduce((m, r) => Math.max(m, r.drivingMinutes), 0);
  const totalStops = routes.reduce((sum, r) => sum + r.stops.length, 0);
  const walkMetresByPickup = routes.flatMap((r) =>
    r.stops.flatMap((s) => s.pickupLegs.map((l) => l.walkMins * WALK_METRES_PER_MINUTE)),
  );
  const totalWalkMetres = walkMetresByPickup.reduce((sum, metres) => sum + metres, 0);
  const maxWalkMetres = walkMetresByPickup.reduce((max, metres) => Math.max(max, metres), 0);
  const participantNames = Object.fromEntries(trip.participants.map((p) => [p.id, p.displayName]));
  const journeys = routes.flatMap((r) => [
    {
      minutes: r.drivingMinutes,
      participantName: r.driverDisplayName,
    },
    ...r.stops.flatMap((stop) =>
      stop.pickupLegs.map((leg) => ({
        minutes:
          leg.walkMins +
          leg.ptMins +
          (minutesBetween(stop.arriveAt, r.destinationArrival) ?? 0),
        participantName: participantNames[leg.participantId],
      })),
    ),
  ]);
  const longestJourney = journeys.reduce(
    (longest, journey) => (journey.minutes > longest.minutes ? journey : longest),
    { minutes: 0, participantName: undefined as string | undefined },
  );
  // Display-only route balance as a 0-1 share index: min/max of per-driver displayed driving
  // minutes. The solver's Fair sharing objective is stricter: it optimises pickup detour above each
  // driver's direct solo trip. The API does not currently expose that unweighted per-driver burden,
  // so keep this as a simple visual summary of route-time balance.
  const drivingMins = routes.map((r) => r.drivingMinutes);
  const maxDrive = drivingMins.length ? Math.max(...drivingMins) : 0;
  const minDrive = drivingMins.length ? Math.min(...drivingMins) : 0;
  const fairnessIndex = routes.length <= 1 || maxDrive <= 0 ? 1 : minDrive / maxDrive;
  return {
    id: s.id,
    label: s.label,
    metrics: {
      totalDrivingMinutes,
      maxDrivingMinutes,
      totalStops,
      totalWalkMetres,
      maxWalkMetres,
      maxJourneyMinutes: longestJourney.minutes,
      maxJourneyParticipantName: longestJourney.participantName,
      fairnessIndex,
    },
    routes,
  };
}
