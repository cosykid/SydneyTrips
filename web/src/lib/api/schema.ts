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

export interface AuthLoginRequest {
  email: string;
  password: string;
}

export interface AuthRegisterRequest {
  email: string;
  password: string;
  displayName: string;
}

export interface AuthResponse {
  token: string;
  expiresAt: IsoDateTime;
  user: {
    id: Uuid;
    email: string;
    displayName: string;
  };
}

export type TripStatus = "draft" | "planned" | "in_progress" | "completed";

export interface TripSummary {
  id: Uuid;
  name: string;
  destinationAddress: string;
  destination: LatLng;
  departAt: IsoDateTime;
  arrivalWindowMinutes: number;
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
  location: LatLng;
  label?: string;
  modality?: "bus_stop" | "train_station" | "ferry_wharf" | "light_rail" | "generic";
}

export interface ObjectiveWeights {
  drivingTime: number;
  stops: number;
  walking: number;
  fairness: number;
}

export const DEFAULT_WEIGHTS: ObjectiveWeights = {
  drivingTime: 0.4,
  stops: 0.2,
  walking: 0.25,
  fairness: 0.15,
};

export interface OptimiseRequest {
  weights: ObjectiveWeights;
  seed?: number;
}

export type RunStatus = "queued" | "running" | "completed" | "failed";

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
}

export interface SolutionStop {
  candidateNodeId?: Uuid;
  location: LatLng;
  arriveAt: IsoDateTime;
  passengerIds: Uuid[];
  walkMetres: number;
}

export interface Solution {
  id: Uuid;
  label: string; // "fastest" | "fewest_stops" | "least_walking" or human label
  metrics: {
    totalDrivingMinutes: number;
    maxDrivingMinutes: number;
    totalStops: number;
    totalWalkMetres: number;
    maxWalkMetres: number;
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
  departAt: IsoDateTime;
  arrivalWindowMinutes: number;
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
  solutionId: Uuid;
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
