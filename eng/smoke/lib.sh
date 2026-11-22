#!/usr/bin/env bash
# Shared helpers for the install-smoke workflow (sourced, not executed). Needs curl and jq.
# The node's local API is loopback-only on MONITOR_APP_ADMIN_PORT (default 55432; see eng/packaging/linux/node.conf).
API="${API:-http://127.0.0.1:${MONITOR_APP_ADMIN_PORT:-55432}}"

log() { printf '[smoke %s] %s\n' "$(date -u +%H:%M:%S)" "$*"; }
fail() { printf '::error::%s\n' "$*"; return 1; }

# Peer id of the local node, empty while the API is not up.
peer_id() { curl -fsS --max-time 5 "$API/api/runtime" 2>/dev/null | jq -r '.localPeer.peerId // empty' 2>/dev/null || true; }

# wait_api SECONDS: waits until /api/runtime reports a local peer id, prints it.
wait_api() {
    local deadline=$((SECONDS + $1)) id=""
    while [ "$SECONDS" -lt "$deadline" ]; do
        id="$(peer_id)"
        if [ -n "$id" ]; then echo "$id"; return 0; fi
        sleep 2
    done
    fail "local API at $API did not report a peer id within $1 s"
}

# state_json: GET /api/v1/state looking back one hour.
state_json() { curl -fsS --max-time 10 "$API/api/v1/state?lookbackSeconds=3600&limit=500"; }

# wait_readings SECONDS: waits for at least one point that carries a real value (Fresh/Stale freshness carries a value).
wait_readings() {
    local deadline=$((SECONDS + $1)) body=""
    while [ "$SECONDS" -lt "$deadline" ]; do
        body="$(state_json 2>/dev/null || true)"
        if [ -n "$body" ] && echo "$body" | jq -e '[.points[] | select(.value != null)] | length > 0' >/dev/null 2>&1; then
            log "readings present: $(echo "$body" | jq -r '[.points[] | select(.value != null)] | length') valued points"
            echo "$body" | jq -c '[.points[] | select(.value != null)][0:5][] | {signalId, subjectEntityId, value, unit, quality, freshness}'
            return 0
        fi
        sleep 3
    done
    log "last state response: ${body:-<none>}"
    fail "no valued readings from /api/v1/state within $1 s"
}

# max_sequence: highest origin sequence currently visible in the state (continues, never resets, across restarts).
max_sequence() { state_json | jq -r '[.points[].sequence] | max // 0'; }
