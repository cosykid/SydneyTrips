"use client";

import {
  useMutation,
  useQuery,
  useQueryClient,
  type UseMutationResult,
  type UseQueryResult,
} from "@tanstack/react-query";
import { apiFetch, ApiError } from "./client";
import {
  apiParticipantToUi,
  apiToCostSplit,
  apiToSolution,
  apiToTrip,
  apiToTripSummary,
} from "./adapters";
import type { components } from "./types";
import type {
  AddParticipantRequest,
  CostSplit,
  CreateTripRequest,
  LockSolutionRequest,
  OptimiseRequest,
  ParetoResponse,
  Participant,
  RunSolutionResponse,
  Solution,
  Trip,
  TripSummary,
  Uuid,
  WhatIfRequest,
} from "./schema";

type ApiTripDto = components["schemas"]["TripDto"];
type ApiTripDetailDto = components["schemas"]["TripDetailDto"];
type ApiParticipantDto = components["schemas"]["ParticipantDto"];
type ApiCostSplitResponse = components["schemas"]["CostSplitResponse"];
type ApiSolutionDto = components["schemas"]["SolutionDto"];

export const tripKeys = {
  all: ["trips"] as const,
  list: () => [...tripKeys.all, "list"] as const,
  detail: (id: Uuid) => [...tripKeys.all, "detail", id] as const,
  run: (tripId: Uuid, runId: Uuid) => [...tripKeys.all, "run", tripId, runId] as const,
  pareto: (tripId: Uuid, runId: Uuid) => [...tripKeys.all, "pareto", tripId, runId] as const,
  costSplit: (tripId: Uuid) => [...tripKeys.all, "cost-split", tripId] as const,
};

export function useTrips(): UseQueryResult<TripSummary[], ApiError> {
  return useQuery({
    queryKey: tripKeys.list(),
    queryFn: async () => {
      const rows = await apiFetch<ApiTripDto[]>("/trips");
      return rows.map(apiToTripSummary);
    },
  });
}

export function useTrip(id: Uuid | undefined): UseQueryResult<Trip, ApiError> {
  return useQuery({
    queryKey: id ? tripKeys.detail(id) : ["trips", "detail", "_"],
    queryFn: async () => {
      const dto = await apiFetch<ApiTripDetailDto>(`/trips/${id}`);
      return apiToTrip(dto);
    },
    enabled: Boolean(id),
  });
}

export function useCreateTrip(): UseMutationResult<TripSummary, ApiError, CreateTripRequest> {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async (body) => {
      // The UI's CreateTripRequest uses arrivalWindowMinutes + destination LatLng;
      // the API wants explicit earliest/latest + flat lon/lat. Translate here so
      // the form component doesn't have to know.
      const departIso = body.departAt;
      const departMs = new Date(departIso).getTime();
      const halfWindowMs = body.arrivalWindowMinutes * 60_000;
      const apiBody = {
        name: body.name,
        destinationName: body.destinationAddress,
        destinationLongitude: body.destination?.lng ?? 0,
        destinationLatitude: body.destination?.lat ?? 0,
        departAt: departIso,
        arrivalWindowEarliest: new Date(departMs + 30 * 60_000 - halfWindowMs).toISOString(),
        arrivalWindowLatest: new Date(departMs + 30 * 60_000 + halfWindowMs).toISOString(),
      };
      const dto = await apiFetch<ApiTripDto>("/trips", { method: "POST", body: apiBody });
      return apiToTripSummary(dto);
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: tripKeys.list() }),
  });
}

interface AddParticipantVars {
  tripId: Uuid;
  body: AddParticipantRequest;
}

export function useAddParticipant(): UseMutationResult<Participant, ApiError, AddParticipantVars> {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async ({ tripId, body }) => {
      // The UI hands us a "driver"/"passenger" role + optional LatLng; the API
      // wants hasCar/seats + flat lon/lat. Translate here.
      const apiBody = {
        displayName: body.displayName,
        homeAddress: body.originAddress,
        homeLongitude: body.origin?.lng ?? null,
        homeLatitude: body.origin?.lat ?? null,
        hasCar: body.role === "driver",
        seats: body.role === "driver" ? body.seatsAvailable ?? 4 : 0,
        preferences: null,
      };
      const dto = await apiFetch<ApiParticipantDto>(`/trips/${tripId}/participants`, {
        method: "POST",
        body: apiBody,
      });
      return apiParticipantToUi(dto);
    },
    onSuccess: (_data, vars) => qc.invalidateQueries({ queryKey: tripKeys.detail(vars.tripId) }),
  });
}

interface RemoveParticipantVars {
  tripId: Uuid;
  participantId: Uuid;
}

export function useRemoveParticipant(): UseMutationResult<void, ApiError, RemoveParticipantVars> {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ tripId, participantId }) =>
      apiFetch<void>(`/trips/${tripId}/participants/${participantId}`, {
        method: "DELETE",
      }),
    onSuccess: (_data, vars) => qc.invalidateQueries({ queryKey: tripKeys.detail(vars.tripId) }),
  });
}

interface OptimiseVars {
  tripId: Uuid;
  body: OptimiseRequest;
}

export function useOptimise(): UseMutationResult<{ runId: Uuid }, ApiError, OptimiseVars> {
  return useMutation({
    mutationFn: ({ tripId, body }) =>
      apiFetch<{ runId: Uuid }>(`/trips/${tripId}/optimise`, {
        method: "POST",
        body,
      }),
  });
}

interface RunVars {
  tripId: Uuid;
  runId: Uuid | undefined;
  pollMs?: number;
}

export function useRun({
  tripId,
  runId,
  pollMs = 1000,
}: RunVars): UseQueryResult<RunSolutionResponse, ApiError> {
  return useQuery({
    queryKey: runId ? tripKeys.run(tripId, runId) : ["trips", "run", tripId, "_"],
    queryFn: () => apiFetch<RunSolutionResponse>(`/trips/${tripId}/runs/${runId}`),
    enabled: Boolean(runId),
    refetchInterval: (query) => {
      const data = query.state.data;
      if (!data) return pollMs;
      return data.status === "completed" || data.status === "failed" ? false : pollMs;
    },
  });
}

export function usePareto(
  tripId: Uuid,
  runId: Uuid | undefined,
): UseQueryResult<ParetoResponse, ApiError> {
  return useQuery({
    queryKey: runId ? tripKeys.pareto(tripId, runId) : ["trips", "pareto", tripId, "_"],
    queryFn: () => apiFetch<ParetoResponse>(`/trips/${tripId}/runs/${runId}/pareto`),
    enabled: Boolean(runId),
  });
}

interface LockVars {
  tripId: Uuid;
  body: LockSolutionRequest;
}

export function useLockSolution(): UseMutationResult<TripSummary, ApiError, LockVars> {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async ({ tripId, body }) => {
      // The UI's LockSolutionRequest exposes a solutionId; the API expects a
      // runId + paretoIndex (since solutions live under runs). The PlanCanvas
      // call site has the runId via active state, but to keep this hook
      // simple we still accept solutionId and let the caller pass runId via
      // body. The API contract is fixed via openapi types.
      const dto = await apiFetch<ApiTripDto>(`/trips/${tripId}/lock-solution`, {
        method: "POST",
        body,
      });
      return apiToTripSummary(dto);
    },
    onSuccess: (_data, vars) => qc.invalidateQueries({ queryKey: tripKeys.detail(vars.tripId) }),
  });
}

export function useCostSplit(tripId: Uuid | undefined): UseQueryResult<CostSplit, ApiError> {
  return useQuery({
    queryKey: tripId ? tripKeys.costSplit(tripId) : ["trips", "cost-split", "_"],
    queryFn: async () => {
      const dto = await apiFetch<ApiCostSplitResponse>(`/trips/${tripId}/cost-split`);
      return apiToCostSplit(dto);
    },
    enabled: Boolean(tripId),
  });
}

// Fetches the locked solution for a trip. The API exposes
// GET /trips/{id}/locked-solution which returns SolutionDto when a solution
// is locked, otherwise 404. We resolve participant names via the trip detail
// query so the FE-shape Solution has driverDisplayName etc.
export function useLockedSolution(
  tripId: Uuid | undefined,
): UseQueryResult<Solution | null, ApiError> {
  const trip = useTrip(tripId);
  return useQuery<Solution | null, ApiError>({
    queryKey: tripId ? [...tripKeys.detail(tripId), "locked-solution"] : ["locked-solution", "_"],
    queryFn: async (): Promise<Solution | null> => {
      if (!trip.data) return null;
      try {
        const dto = await apiFetch<ApiSolutionDto>(`/trips/${tripId}/locked-solution`);
        return apiToSolution(dto, trip.data);
      } catch (err) {
        if (err instanceof ApiError && (err.status === 404 || err.status === 405)) {
          return null;
        }
        throw err;
      }
    },
    enabled: Boolean(tripId) && trip.isSuccess && Boolean(trip.data?.lockedSolutionId),
  });
}

interface WhatIfVars {
  tripId: Uuid;
  body: WhatIfRequest;
}

interface WhatIfResponse {
  runId: Uuid;
}

export function useWhatIf(): UseMutationResult<WhatIfResponse, ApiError, WhatIfVars> {
  return useMutation({
    mutationFn: ({ tripId, body }) =>
      apiFetch<WhatIfResponse>(`/trips/${tripId}/whatif`, {
        method: "POST",
        body,
      }),
  });
}
