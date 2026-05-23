#!/usr/bin/env bash
# WS7 smoke test: exercise the cost-split, return-leg, what-if, and calendar.ics endpoints
# end-to-end against a running Trips.Api.
#
# Prerequisites:
#   - infra/docker-compose.yml has been brought up (postgres + redis)
#   - migrations have been applied (`dotnet ef database update --project src/Trips.Data`)
#   - the API is running on http://localhost:5000 (or override via BASE_URL)
#
# Usage:
#   ./tests/smoke/ws7.sh
#   BASE_URL=http://localhost:5050 ./tests/smoke/ws7.sh
set -euo pipefail

BASE_URL=${BASE_URL:-http://localhost:5000}
EMAIL="ws7-smoke-$(date +%s)@example.com"
PASSWORD="password123"
NAME="WS7 Smoke User"

say() { printf '\n>>> %s\n' "$*"; }
need_jq() { command -v jq >/dev/null || { echo "jq is required" >&2; exit 1; }; }
need_jq

say "register user"
curl -sf -X POST "$BASE_URL/auth/register" \
  -H 'Content-Type: application/json' \
  -d "$(jq -nc --arg e "$EMAIL" --arg p "$PASSWORD" --arg n "$NAME" \
        '{email:$e,password:$p,displayName:$n}')" \
  > /tmp/ws7-register.json
TOKEN=$(jq -r .accessToken /tmp/ws7-register.json)
[[ -n "$TOKEN" && "$TOKEN" != "null" ]] || { echo "missing access token" >&2; exit 1; }
AUTH=(-H "Authorization: Bearer $TOKEN")

say "create trip"
TRIP_PAYLOAD=$(jq -nc \
  --arg depart "$(date -u -v+1d '+%Y-%m-%dT%H:%M:%SZ' 2>/dev/null || date -u -d '+1 day' '+%Y-%m-%dT%H:%M:%SZ')" \
  --arg earliest "$(date -u -v+1d '+%Y-%m-%dT%H:%M:%SZ' 2>/dev/null || date -u -d '+1 day' '+%Y-%m-%dT%H:%M:%SZ')" \
  --arg latest "$(date -u -v+1d -v+2H '+%Y-%m-%dT%H:%M:%SZ' 2>/dev/null || date -u -d '+1 day +2 hours' '+%Y-%m-%dT%H:%M:%SZ')" \
  '{
    name:"WS7 Smoke Trip",
    destinationName:"Palm Beach",
    destinationLongitude:151.3247,
    destinationLatitude:-33.5984,
    departAt:$depart,
    arrivalWindowEarliest:$earliest,
    arrivalWindowLatest:$latest
  }')
curl -sf -X POST "$BASE_URL/trips" \
  "${AUTH[@]}" -H 'Content-Type: application/json' \
  -d "$TRIP_PAYLOAD" > /tmp/ws7-trip.json
TRIP_ID=$(jq -r .id /tmp/ws7-trip.json)
echo "trip: $TRIP_ID"

say "add driver + passenger"
curl -sf -X POST "$BASE_URL/trips/$TRIP_ID/participants" \
  "${AUTH[@]}" -H 'Content-Type: application/json' \
  -d '{
    "displayName":"Driver",
    "homeLongitude":151.2093,
    "homeLatitude":-33.8688,
    "hasCar":true,
    "seats":4
  }' > /tmp/ws7-driver.json
DRIVER_ID=$(jq -r .id /tmp/ws7-driver.json)

curl -sf -X POST "$BASE_URL/trips/$TRIP_ID/participants" \
  "${AUTH[@]}" -H 'Content-Type: application/json' \
  -d '{
    "displayName":"Passenger",
    "homeLongitude":151.2100,
    "homeLatitude":-33.8700,
    "hasCar":false,
    "seats":0
  }' > /tmp/ws7-passenger.json
PASS_ID=$(jq -r .id /tmp/ws7-passenger.json)
echo "driver=$DRIVER_ID passenger=$PASS_ID"

say "optimise (heuristic solver — faster than OR-Tools and avoids the FK quirk in Phase B's runner)"
OPT_PAYLOAD='{"weights":{"driveTime":1,"stopCount":0.5,"walkAndPt":0.5,"arrivalSpread":0.3,"fairness":0.3},"solver":1}'
curl -sf -X POST "$BASE_URL/trips/$TRIP_ID/optimise" \
  "${AUTH[@]}" -H 'Content-Type: application/json' \
  -d "$OPT_PAYLOAD" > /tmp/ws7-run.json
RUN_ID=$(jq -r .runId /tmp/ws7-run.json)
echo "run: $RUN_ID"

say "poll run until complete"
for _ in $(seq 1 60); do
  STATUS=$(curl -sf "$BASE_URL/trips/$TRIP_ID/runs/$RUN_ID" "${AUTH[@]}" | jq -r .run.status)
  echo "  status=$STATUS"
  # OptimisationStatus: Completed = 2 (also accept "Completed" string for future-proofing).
  [[ "$STATUS" == "2" || "$STATUS" == "Completed" ]] && break
  [[ "$STATUS" == "3" || "$STATUS" == "Failed" ]] && { echo "run failed"; exit 1; }
  sleep 0.5
done

say "lock solution"
curl -sf -X POST "$BASE_URL/trips/$TRIP_ID/lock-solution" \
  "${AUTH[@]}" -H 'Content-Type: application/json' \
  -d "$(jq -nc --arg r "$RUN_ID" '{runId:$r,paretoIndex:0}')" \
  > /tmp/ws7-lock.json
echo "locked solution id: $(jq -r .lockedSolutionId /tmp/ws7-lock.json)"

say "cost-split (defaults)"
curl -sf "$BASE_URL/trips/$TRIP_ID/cost-split" "${AUTH[@]}" | jq

say "cost-split (override fuel + tolls)"
curl -sf -X POST "$BASE_URL/trips/$TRIP_ID/cost-split" \
  "${AUTH[@]}" -H 'Content-Type: application/json' \
  -d '{"fuelPricePerLitre":1.99,"fuelEconomyLPer100Km":7.5,"tolls":[]}' | jq

say "return-leg (two passengers in one cluster)"
curl -sf -X POST "$BASE_URL/trips/$TRIP_ID/return-leg" \
  "${AUTH[@]}" -H 'Content-Type: application/json' \
  -d "$(jq -nc --arg p1 "$PASS_ID" \
    '{requests:[{participantId:$p1,desiredDeparture:"2026-05-23T17:00:00Z",dropoffLongitude:151.21,dropoffLatitude:-33.87}]}')" | jq '.solutions | length'

say "what-if (no drop, new weights)"
curl -sf -X POST "$BASE_URL/trips/$TRIP_ID/whatif" \
  "${AUTH[@]}" -H 'Content-Type: application/json' \
  -d '{"newWeights":{"driveTime":0.5,"stopCount":0.2,"walkAndPt":1.0,"arrivalSpread":0.3,"fairness":0.3}}' | jq

say "calendar.ics for the driver"
curl -sf "$BASE_URL/trips/$TRIP_ID/participants/$DRIVER_ID/calendar.ics" "${AUTH[@]}" -o /tmp/ws7.ics
head -10 /tmp/ws7.ics
grep -q 'BEGIN:VEVENT' /tmp/ws7.ics && echo "  VEVENT present"

say "DONE"
