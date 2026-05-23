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
type ApiRunWithSolutionDto = components["schemas"]["OptimisationRunDtoWithSolution"];

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
      // The UI expresses target time as `arriveBy`; the API still has a
      // `departAt` field (treated as the earliest possible trip start) plus
      // an explicit arrival window. We give `departAt` a 60-minute head-start
      // before the target arrival so the solver has room to schedule each
      // driver's actual departure based on their route length.
      const arrivalMs = new Date(body.arriveBy).getTime();
      const halfWindowMs = body.arrivalWindowMinutes * 60_000;
      const departMs = arrivalMs - 60 * 60_000;
      const apiBody = {
        name: body.name,
        destinationName: body.destinationAddress,
        destinationLongitude: body.destination?.lng ?? 0,
        destinationLatitude: body.destination?.lat ?? 0,
        departAt: new Date(departMs).toISOString(),
        arrivalWindowEarliest: new Date(arrivalMs - halfWindowMs).toISOString(),
        arrivalWindowLatest: new Date(arrivalMs + halfWindowMs).toISOString(),
      };
      const dto = await apiFetch<ApiTripDto>("/trips", { method: "POST", body: apiBody });
      return apiToTripSummary(dto);
    },
    onSuccess: () => qc.invalidateQueries({ queryKey: tripKeys.list() }),
  });
}

interface UpdateDestinationVars {
  tripId: Uuid;
  destinationAddress: string;
  destination: { lat: number; lng: number };
}

export function useUpdateTripDestination(): UseMutationResult<
  TripSummary,
  ApiError,
  UpdateDestinationVars
> {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: async ({ tripId, destinationAddress, destination }) => {
      const apiBody = {
        destinationName: destinationAddress,
        destinationLongitude: destination.lng,
        destinationLatitude: destination.lat,
      };
      const dto = await apiFetch<ApiTripDto>(`/trips/${tripId}/destination`, {
        method: "PATCH",
        body: apiBody,
      });
      return apiToTripSummary(dto);
    },
    onSuccess: (_data, vars) => {
      qc.invalidateQueries({ queryKey: tripKeys.detail(vars.tripId) });
      qc.invalidateQueries({ queryKey: tripKeys.list() });
    },
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
  /** Pass the trip so the inline `solution` can be adapted to UI shape. */
  trip?: Trip;
}

export function useRun({
  tripId,
  runId,
  pollMs = 1000,
  trip,
}: RunVars): UseQueryResult<RunSolutionResponse, ApiError> {
  return useQuery({
    queryKey: runId ? tripKeys.run(tripId, runId) : ["trips", "run", tripId, "_"],
    // The wire shape is `{ run: OptimisationRunDto, solution?: SolutionDto }`;
    // flatten it here so callers can read `status`/`error`/`solution` directly
    // without having to know about the API's composite envelope. `solution`
    // is adapted via `apiToSolution` when we have trip context — when we
    // don't (e.g. background polling before the trip query resolves) we leave
    // it raw and let the consumer skip displaying it.
    queryFn: async (): Promise<RunSolutionResponse> => {
      const dto = await apiFetch<ApiRunWithSolutionDto>(
        `/trips/${tripId}/runs/${runId}`,
      );
      return {
        id: dto.run.id,
        tripId: dto.run.tripId,
        status: dto.run.status,
        createdAt: dto.run.startedAt,
        completedAt: dto.run.completedAt ?? undefined,
        error: dto.run.failureReason ?? undefined,
        solution:
          dto.solution && trip ? apiToSolution(dto.solution, trip) : undefined,
      };
    },
    enabled: Boolean(runId),
    // 4xx errors (most commonly 404 for a stale/missing runId) are terminal —
    // don't retry into a hot loop and don't keep polling.
    retry: (failureCount, error) => {
      if (error instanceof ApiError && error.status >= 400 && error.status < 500) {
        return false;
      }
      return failureCount < 3;
    },
    refetchInterval: (query) => {
      if (query.state.status === "error") return false;
      const data = query.state.data;
      if (!data) return pollMs;
      return data.status === "completed" ||
        data.status === "failed" ||
        data.status === "cancelled"
        ? false
        : pollMs;
    },
  });
}

export function usePareto(
  tripId: Uuid,
  runId: Uuid | undefined,
  trip?: Trip,
): UseQueryResult<ParetoResponse, ApiError> {
  return useQuery({
    queryKey: runId ? tripKeys.pareto(tripId, runId) : ["trips", "pareto", tripId, "_"],
    // Wire shape is `SolutionDto[]`; wrap as `{ runId, solutions }` and adapt
    // each entry via `apiToSolution` so the UI gets metrics + driver display
    // names. Disabled until we have a trip to adapt against.
    queryFn: async (): Promise<ParetoResponse> => {
      const arr = await apiFetch<ApiSolutionDto[]>(
        `/trips/${tripId}/runs/${runId}/pareto`,
      );
      return {
        runId: runId as Uuid,
        solutions: trip ? arr.map((s) => apiToSolution(s, trip)) : [],
      };
    },
    enabled: Boolean(runId) && Boolean(trip),
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
