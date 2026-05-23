"use client";

import {
  useMutation,
  useQuery,
  useQueryClient,
  type UseMutationResult,
  type UseQueryResult,
} from "@tanstack/react-query";
import { apiFetch, ApiError } from "./client";
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
    queryFn: () => apiFetch<TripSummary[]>("/trips"),
  });
}

export function useTrip(id: Uuid | undefined): UseQueryResult<Trip, ApiError> {
  return useQuery({
    queryKey: id ? tripKeys.detail(id) : ["trips", "detail", "_"],
    queryFn: () => apiFetch<Trip>(`/trips/${id}`),
    enabled: Boolean(id),
  });
}

export function useCreateTrip(): UseMutationResult<TripSummary, ApiError, CreateTripRequest> {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: (body) =>
      apiFetch<TripSummary>("/trips", {
        method: "POST",
        body,
      }),
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
    mutationFn: ({ tripId, body }) =>
      apiFetch<Participant>(`/trips/${tripId}/participants`, {
        method: "POST",
        body,
      }),
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

export function useLockSolution(): UseMutationResult<Trip, ApiError, LockVars> {
  const qc = useQueryClient();
  return useMutation({
    mutationFn: ({ tripId, body }) =>
      apiFetch<Trip>(`/trips/${tripId}/lock-solution`, {
        method: "POST",
        body,
      }),
    onSuccess: (_data, vars) => qc.invalidateQueries({ queryKey: tripKeys.detail(vars.tripId) }),
  });
}

export function useCostSplit(tripId: Uuid | undefined): UseQueryResult<CostSplit, ApiError> {
  return useQuery({
    queryKey: tripId ? tripKeys.costSplit(tripId) : ["trips", "cost-split", "_"],
    queryFn: () => apiFetch<CostSplit>(`/trips/${tripId}/cost-split`),
    enabled: Boolean(tripId),
  });
}

// Fetches the locked solution for a trip. WS7 may surface this as a single
// endpoint; until then we look it up by solution id which the API exposes on
// the trip resource. Returns `undefined` when nothing is locked yet.
export function useLockedSolution(
  tripId: Uuid | undefined,
): UseQueryResult<Solution | null, ApiError> {
  return useQuery<Solution | null, ApiError>({
    queryKey: tripId ? [...tripKeys.detail(tripId), "locked-solution"] : ["locked-solution", "_"],
    queryFn: async (): Promise<Solution | null> => {
      // Try the dedicated endpoint first; fall back to the legacy path if the
      // API hasn't shipped it yet. Both shapes are tolerated.
      try {
        return await apiFetch<Solution>(`/trips/${tripId}/solution`);
      } catch (err) {
        if (err instanceof ApiError && (err.status === 404 || err.status === 405)) {
          return null;
        }
        throw err;
      }
    },
    enabled: Boolean(tripId),
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
