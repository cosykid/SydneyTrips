// TODO: replace with codegen output from `npm run gen:api` once WS4 ships
// `src/Trips.Api/openapi.json`. The shapes below are hand-written to match the
// endpoint list in `me-i-want-to-abstract-dragonfly.md` / WS4 plan, so the
// frontend can compile and the wire format won't drift far from the real one.

export type Uuid = string;
export type IsoDateTime = string; // ISO-8601 string

export interface LatLng {
  lat: number;
  lng: number;
}

// Auth types removed — the API now uses an anonymous-session cookie minted by
// AnonymousSessionMiddleware. No tokens, no login/register payloads.

export type TripStatus = "draft" | "planned" | "in_progress" | "completed";

export interface TripSummary {
  id: Uuid;
  name: string;
  destinationAddress: string;
  destination: LatLng;
  /** When the user wants to be at the destination. */
  arriveBy: IsoDateTime;
  status: TripStatus;
  participantCount: number;
  hasLockedSolution: boolean;
}

export type ParticipantRole = "driver" | "passenger";

export interface Participant {
  id: Uuid;
  displayName: string;
  email?: string;
  role: ParticipantRole;
  originAddress: string;
  origin: LatLng;
  seatsAvailable?: number; // drivers only
  prefs?: ParticipantPrefs;
}

export interface ParticipantPrefs {
  maxWalkMetres?: number;
  maxDetourMinutes?: number;
}

export interface Trip extends TripSummary {
  participants: Participant[];
  candidateNodes: CandidateNode[];
  lockedSolutionId?: Uuid;
}

export interface CandidateNode {
  id: Uuid;
  /** The participant this candidate node belongs to — candidate sets are per-participant,
   *  not shared, so this is needed when matching a Stop's candidateNodeId to a passenger's
   *  walk/PT split. */
  participantId?: Uuid;
  location: LatLng;
  label?: string;
  modality?: "bus_stop" | "train_station" | "ferry_wharf" | "light_rail" | "generic";
  /** Walking minutes from the participant's home to this node (0 for the Home node itself). */
  walkMins?: number;
  /** Public-transport minutes from home (bus / train / ferry / light rail). Non-zero indicates
   *  the passenger is expected to take PT — pure-walk candidates have ptMins = 0. */
  ptMins?: number;
  /** Ordered polyline of the participant's PT journey from home to this hub, in
   *  [lng, lat] form per point. Undefined for the Home node and for nodes generated
   *  before the polyline feature shipped (legacy DB rows). When present, the map
   *  draws this instead of a crow-fly straight line. */
  path?: LatLng[];
}

export interface ObjectiveWeights {
  driverBias: number;
  stops: number;
  fairness: number;
}

export const DEFAULT_WEIGHTS: ObjectiveWeights = {
  driverBias: 0.5,
  stops: 0.2,
  fairness: 0.15,
};

export interface OptimiseRequest {
  weights: ObjectiveWeights;
  seed?: number;
}

export type RunStatus = "pending" | "running" | "completed" | "failed" | "cancelled";

export interface RunResource {
  id: Uuid;
  tripId: Uuid;
  status: RunStatus;
  createdAt: IsoDateTime;
  completedAt?: IsoDateTime;
  error?: string;
}

export interface SolutionRoute {
  driverParticipantId: Uuid;
  driverDisplayName: string;
  colour: string; // categorical palette colour, hex
  polyline: LatLng[]; // ordered points along the driver's route
  stops: SolutionStop[];
  drivingMinutes: number;
  drivingDistanceKm: number;
  /** Estimated clock time the driver reaches the destination, on the same timeline as each stop's
   *  `arriveAt` and `departure`. The backend anchors it a few minutes before the arrival target so
   *  the driver lands roughly on time. Undefined when the backend couldn't anchor it. */
  destinationArrival?: IsoDateTime;
  /** Estimated clock time the driver leaves home (`destinationArrival − drivingMinutes`). The
   *  timeline slides later than the scheduled trip start so the driver departs just-in-time instead
   *  of arriving far too early. Undefined when the backend couldn't anchor it. */
  departure?: IsoDateTime;
}

export interface SolutionStop {
  candidateNodeId?: Uuid;
  location: LatLng;
  arriveAt: IsoDateTime;
  /** Modality of the candidate node served at this stop. UI uses this to pick a transit icon
   *  on hubs (train station / bus stop / ferry / light rail). */
  nodeKind?: CandidateNode["modality"];
  /** Per-passenger pickup detail — supersedes the bare `passengerIds` list, but we keep that
   *  for legacy adapters until they're migrated. */
  pickupLegs: PickupLeg[];
  /** Legacy aggregate view — list of participant ids picked up here. Derived from pickupLegs. */
  passengerIds: Uuid[];
  /** Approximate walking distance for backward compatibility; new code should sum each leg's
   *  walkMins instead. */
  walkMetres: number;
}

/** One passenger's home → pickup leg. Walking + PT minutes are reported separately so the UI
 *  can render the two segments distinctly. `path` is this passenger's own home→pickup geometry
 *  (when the backend has it) — carried per-leg so co-located passengers each draw their own route
 *  rather than sharing the stop's single canonical path. */
export interface PickupLeg {
  participantId: Uuid;
  walkMins: number;
  ptMins: number;
  path?: LatLng[];
  /** The same home→pickup journey as `path`, split into mode-tagged segments so the map can
   *  colour each leg (walk / train / bus / ferry / light rail) distinctly, Google-Maps style.
   *  Absent on legacy / stub data — fall back to the single-colour `path`. */
  pathLegs?: PathLeg[];
}

/** One mode-tagged segment of a passenger's home→pickup journey. The fields after `path` back the
 *  Google-Maps-style timed itinerary shown on hover — all optional, since stub / pre-feature data
 *  carries only mode + geometry. */
export interface PathLeg {
  /** Raw TfNSW mode: "walk" | "train" | "metro" | "bus" | "ferry" | "lightrail" | "unknown". */
  mode: string;
  path: LatLng[];
  /** Travel time of this leg in minutes. */
  durationMins?: number;
  /** Stop name this leg departs from (e.g. "Kingsford Light Rail"). */
  fromName?: string;
  /** Stop name this leg arrives at. */
  toName?: string;
  /** Line label for PT legs (e.g. "T1", "L2", "389"). Absent on walk legs. */
  routeShortName?: string;
  /** Scheduled clock time this leg departs its origin (ISO-8601). */
  departureTime?: IsoDateTime;
  /** Scheduled clock time this leg reaches its destination (ISO-8601). */
  arrivalTime?: IsoDateTime;
}

export interface Solution {
  id: Uuid;
  label: string; // "fastest" | "fewest_stops" | "least_transit" or human label
  metrics: {
    totalDrivingMinutes: number;
    maxDrivingMinutes: number;
    totalStops: number;
    totalWalkMetres: number;
    maxWalkMetres: number;
    maxJourneyMinutes: number;
    maxJourneyParticipantName?: string;
    fairnessIndex: number;
  };
  routes: SolutionRoute[];
}

export interface RunSolutionResponse extends RunResource {
  solution?: Solution;
}

export interface ParetoResponse {
  runId: Uuid;
  solutions: Solution[];
}

export interface CreateTripRequest {
  name: string;
  destinationAddress: string;
  destination?: LatLng; // optional geocoded pin if frontend resolves
  /** User's target arrival time. The hook adapter back-computes a sensible
   *  departAt before sending to the API. */
  arriveBy: IsoDateTime;
}

export interface AddParticipantRequest {
  displayName: string;
  email?: string;
  role: ParticipantRole;
  originAddress: string;
  origin?: LatLng;
  seatsAvailable?: number;
  prefs?: ParticipantPrefs;
}

export interface LockSolutionRequest {
  /** Solutions live under runs, so the API locks by run + index, not by a bare
   *  solution id. With the single-solution-per-run model this is always index 0. */
  runId: Uuid;
  paretoIndex: number;
}

export interface CostSplit {
  tripId: Uuid;
  currency: "AUD";
  totalCost: number;
  perParticipant: Array<{
    participantId: Uuid;
    displayName: string;
    amount: number;
    breakdown: { fuel: number; tolls: number };
  }>;
}

export interface WhatIfAddParticipant {
  displayName: string;
  email?: string;
  role: ParticipantRole;
  originAddress: string;
  origin?: LatLng;
  seatsAvailable?: number;
  prefs?: ParticipantPrefs;
}

export interface WhatIfRequest {
  /** Participants to remove from the existing locked solution. */
  dropParticipantIds: Uuid[];
  /** New participants to add (will be geocoded server-side if no point given). */
  addParticipants: WhatIfAddParticipant[];
  /** Optional new objective weights. */
  newWeights?: ObjectiveWeights;
  /** When true, the solver warm-starts from the current locked solution to
   * minimise churn (kept stops stay put where possible). */
  repair?: boolean;
}

export interface CostSplitFuelEconomy {
  /** Cost per litre in A$. */
  fuelPricePerLitre: number;
  /** Fuel economy in litres per 100 km. */
  litresPer100Km: number;
}

export const DEFAULT_COST_INPUTS: CostSplitFuelEconomy = {
  fuelPricePerLitre: 2.1,
  litresPer100Km: 8.5,
};
