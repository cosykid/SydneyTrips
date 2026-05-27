#!/usr/bin/env bash
# Seed a demo "Group trip to Palm Beach" scenario against a running Trips.Api.
#
# Idempotent: re-running with the same DEMO_EMAIL drops any existing trip with
# the same name owned by that user before re-creating, so the screenshot run
# always starts from a known state. Pass --reset to wipe and start over even if
# nothing has changed.
#
# Prerequisites:
#   - docker compose -f infra/docker-compose.yml up -d         (Postgres + Redis healthy)
#   - dotnet ef database update --project src/Trips.Data --startup-project src/Trips.Api
#   - dotnet run --project src/Trips.Api                       (running on $BASE_URL)
#
# Outputs (written to /tmp by default, override with DEMO_OUT_DIR):
#   /tmp/seed-demo.json   { sessionId, email, password, tripId, runId, lockedSolutionId, driverIds, passengerIds }
#
# Used by:
#   - web/tests/screenshots.spec.ts            (logs in, navigates each page)
#   - web/tests/e2e/single-driver.spec.ts      (uses a slimmed seed variant)
#   - web/tests/e2e/multi-driver.spec.ts       (uses the full seed)
#   - web/tests/e2e/what-if.spec.ts            (uses the full seed)
#
# Usage:
#   ./tests/seed/seed-demo.sh
#   ./tests/seed/seed-demo.sh --reset
#   DEMO_EMAIL=alice@example.com BASE_URL=http://localhost:5050 ./tests/seed/seed-demo.sh
#   SCENARIO=single ./tests/seed/seed-demo.sh    # 1 driver, 3 passengers
#   SCENARIO=multi  ./tests/seed/seed-demo.sh    # 3 drivers, 12 passengers (default)
#
set -euo pipefail

BASE_URL=${BASE_URL:-http://localhost:5000}
# Auth was removed — the script now drives the API with a single anonymous-session
# cookie (curl -b/-c). DEMO_EMAIL/PASSWORD/NAME are kept in the output JSON for
# back-compat with downstream consumers (Playwright specs etc.) that still read
# the fields; they're no longer used to authenticate.
DEMO_EMAIL=${DEMO_EMAIL:-demo@sydneytrips.dev}
DEMO_PASSWORD=${DEMO_PASSWORD:-PalmBeach2026!}
DEMO_NAME=${DEMO_NAME:-"Demo Organiser"}
SCENARIO=${SCENARIO:-multi}
COOKIE_JAR=$(mktemp -t seed-demo-cookies)
trap 'rm -f "$COOKIE_JAR"' EXIT
DEMO_OUT_DIR=${DEMO_OUT_DIR:-/tmp}
TRIP_NAME=${TRIP_NAME:-"Group trip to Palm Beach"}
RESET=0

for arg in "$@"; do
  case "$arg" in
    --reset) RESET=1 ;;
    *) echo "unknown arg: $arg" >&2; exit 2 ;;
  esac
done

command -v jq >/dev/null || { echo "jq is required (brew install jq)" >&2; exit 1; }

say()  { printf '\n>>> %s\n' "$*"; }
note() { printf '    %s\n' "$*"; }

OUT_JSON="$DEMO_OUT_DIR/seed-demo.json"

# Sydney sample addresses + coords (lng, lat) sourced from open data / Google Maps.
# Destination: Palm Beach (Barrenjoey / Pittwater).
DEST_NAME="Palm Beach NSW"
DEST_LNG=151.3247
DEST_LAT=-33.5984

# Drivers (12 candidates — we pick `seats` depending on scenario).
# Spread roughly across four corners of Sydney: east, north, inner-west, south.
DRIVERS=(
  "Alex (Bondi)|151.2767|-33.8915|4"
  "Bianca (Chatswood)|151.1833|-33.7969|4"
  "Cameron (Newtown)|151.1798|-33.8975|3"
  "Dani (Hurstville)|151.1027|-33.9663|4"
)

# Passengers (16 candidates — pick first N depending on scenario).
PASSENGERS=(
  "Eli (Coogee)|151.2576|-33.9211"
  "Fiona (Manly)|151.2868|-33.7969"
  "Gus (Mosman)|151.2378|-33.8281"
  "Hana (Surry Hills)|151.2125|-33.8841"
  "Ivy (Glebe)|151.1856|-33.8786"
  "Jaz (Marrickville)|151.1551|-33.9099"
  "Kai (Rockdale)|151.1390|-33.9522"
  "Leo (Strathfield)|151.0833|-33.8736"
  "Mia (Parramatta)|151.0034|-33.8150"
  "Nate (Burwood)|151.1037|-33.8780"
  "Oli (Ashfield)|151.1244|-33.8881"
  "Priya (Auburn)|151.0322|-33.8492"
)

case "$SCENARIO" in
  single)
    N_DRIVERS=1
    N_PASSENGERS=3
    ;;
  multi)
    N_DRIVERS=3
    N_PASSENGERS=8
    ;;
  *)
    echo "unknown SCENARIO: $SCENARIO (use single|multi)" >&2
    exit 2
    ;;
esac

say "Trips API base URL: $BASE_URL"
note "scenario: $SCENARIO ($N_DRIVERS drivers, $N_PASSENGERS passengers)"
note "demo user: $DEMO_EMAIL"

# --- helpers ---------------------------------------------------------------

api_post()   { curl -sf -b "$COOKIE_JAR" -c "$COOKIE_JAR" -X POST "$BASE_URL$1" -H 'Content-Type: application/json' "${@:2}"; }
api_get()    { curl -sf -b "$COOKIE_JAR" -c "$COOKIE_JAR"           "$BASE_URL$1" "${@:2}"; }
api_delete() { curl -sf -b "$COOKIE_JAR" -c "$COOKIE_JAR" -X DELETE "$BASE_URL$1" "${@:2}"; }

# --- prime the anonymous session cookie ------------------------------------

say "prime anonymous session cookie"
api_get "/healthz" >/dev/null
# The trips_session cookie is now in the jar (curl marks httpOnly cookies with a
# #HttpOnly_ prefix on the domain field; awk's whitespace split still puts the name
# in $6 and the value in $7). Downstream consumers (Playwright screenshots) inject
# this value so the browser acts as the same anonymous owner that seeded the trip.
SESSION_ID=$(awk '$6 == "trips_session" { print $7 }' "$COOKIE_JAR" | tail -1)
[[ -n "$SESSION_ID" ]] || { echo "failed to capture trips_session cookie" >&2; exit 1; }
note "session: $SESSION_ID"
# AUTH array used to carry the Bearer header; auth is gone, so we leave it
# empty for any remaining "${AUTH[@]}" expansions further down.
AUTH=()

# --- reset existing trip with the same name (idempotency) ------------------

say "look for existing demo trip"
existing=$(api_get "/trips" | jq --arg n "$TRIP_NAME" '[.[] | select(.name == $n)] | .[0].id // empty' -r)
if [[ -n "$existing" ]]; then
  if [[ "$RESET" -eq 1 ]] || true; then
    note "existing trip $existing — deleting for a clean seed"
    api_delete "/trips/$existing" >/dev/null || true
  fi
fi

# --- create trip -----------------------------------------------------------

DEPART_AT=$(date -u -v+1d -v9H -v0M -v0S '+%Y-%m-%dT%H:%M:%SZ' 2>/dev/null || date -u -d '+1 day 09:00' '+%Y-%m-%dT%H:%M:%SZ')
WINDOW_EARLY=$(date -u -v+1d -v9H -v45M -v0S '+%Y-%m-%dT%H:%M:%SZ' 2>/dev/null || date -u -d '+1 day 09:45' '+%Y-%m-%dT%H:%M:%SZ')
WINDOW_LATE=$(date -u -v+1d -v10H -v15M -v0S '+%Y-%m-%dT%H:%M:%SZ' 2>/dev/null || date -u -d '+1 day 10:15' '+%Y-%m-%dT%H:%M:%SZ')

say "create trip '$TRIP_NAME'"
TRIP_JSON=$(api_post "/trips" \
  -d "$(jq -nc \
      --arg name "$TRIP_NAME" --arg dest "$DEST_NAME" \
      --argjson dlng "$DEST_LNG" --argjson dlat "$DEST_LAT" \
      --arg dep "$DEPART_AT" --arg early "$WINDOW_EARLY" --arg late "$WINDOW_LATE" \
      '{name:$name,destinationName:$dest,destinationLongitude:$dlng,destinationLatitude:$dlat,departAt:$dep,arrivalWindowEarliest:$early,arrivalWindowLatest:$late}')")
TRIP_ID=$(echo "$TRIP_JSON" | jq -r .id)
note "trip: $TRIP_ID"

# --- add drivers + passengers ---------------------------------------------

declare -a DRIVER_IDS=()
declare -a PASSENGER_IDS=()

add_participant() {
  local name="$1" lng="$2" lat="$3" has_car="$4" seats="$5"
  api_post "/trips/$TRIP_ID/participants" \
    -d "$(jq -nc \
        --arg n "$name" --argjson lng "$lng" --argjson lat "$lat" \
        --argjson hc "$has_car" --argjson s "$seats" \
        '{displayName:$n,homeLongitude:$lng,homeLatitude:$lat,hasCar:$hc,seats:$s}')" \
    | jq -r .id
}

say "add $N_DRIVERS drivers"
for ((i=0; i<N_DRIVERS; i++)); do
  IFS='|' read -r name lng lat seats <<< "${DRIVERS[$i]}"
  pid=$(add_participant "$name" "$lng" "$lat" true "$seats")
  DRIVER_IDS+=("$pid")
  note "  driver: $name -> $pid"
done

say "add $N_PASSENGERS passengers"
for ((i=0; i<N_PASSENGERS; i++)); do
  IFS='|' read -r name lng lat <<< "${PASSENGERS[$i]}"
  pid=$(add_participant "$name" "$lng" "$lat" false 0)
  PASSENGER_IDS+=("$pid")
  note "  passenger: $name -> $pid"
done

# --- optimise (heuristic solver — fast and avoids the OptimisationRunner FK quirk) ---

say "optimise"
OPT_PAYLOAD='{"weights":{"driveTime":1,"stopCount":0.5,"walkAndPt":0.5,"arrivalSpread":0.3,"fairness":0.3},"solver":1}'
RUN_ID=$(api_post "/trips/$TRIP_ID/optimise" -d "$OPT_PAYLOAD" | jq -r .runId)
note "run: $RUN_ID"

say "poll until complete"
STATUS="unknown"
for _ in $(seq 1 60); do
  STATUS=$(api_get "/trips/$TRIP_ID/runs/$RUN_ID" | jq -r .run.status)
  note "  status=$STATUS"
  [[ "$STATUS" == "2" || "$STATUS" == "Completed" ]] && break
  [[ "$STATUS" == "3" || "$STATUS" == "Failed" ]] && { echo "run failed" >&2; exit 1; }
  sleep 0.5
done
[[ "$STATUS" == "2" || "$STATUS" == "Completed" ]] || { echo "run did not finish" >&2; exit 1; }

# --- lock a solution -------------------------------------------------------

say "lock solution (pareto index 0 = balanced)"
LOCKED=$(api_post "/trips/$TRIP_ID/lock-solution" \
  -d "$(jq -nc --arg r "$RUN_ID" '{runId:$r,paretoIndex:0}')" \
  | jq -r .lockedSolutionId)
note "locked solution: $LOCKED"

# --- write outputs ---------------------------------------------------------

jq -n \
  --arg session "$SESSION_ID" \
  --arg email "$DEMO_EMAIL" --arg password "$DEMO_PASSWORD" \
  --arg base "$BASE_URL" --arg trip "$TRIP_ID" --arg run "$RUN_ID" --arg lockid "$LOCKED" \
  --argjson drivers "$(printf '%s\n' "${DRIVER_IDS[@]}" | jq -R . | jq -s .)" \
  --argjson passengers "$(printf '%s\n' "${PASSENGER_IDS[@]}" | jq -R . | jq -s .)" \
  '{
    baseUrl: $base,
    sessionId: $session,
    email: $email,
    password: $password,
    tripId: $trip,
    runId: $run,
    lockedSolutionId: $lockid,
    driverIds: $drivers,
    passengerIds: $passengers
  }' > "$OUT_JSON"

say "DONE"
note "wrote $OUT_JSON"
cat "$OUT_JSON"
