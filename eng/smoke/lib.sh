#!/usr/bin/env bash
# Shared helpers for the install-smoke workflow (sourced, not executed). Needs curl and jq.
# The node's local API is loopback-only on MONITOR_APP_ADMIN_PORT (default 55432; see eng/packaging/linux/node.conf).
API="${API:-http://127.0.0.1:${MONITOR_APP_ADMIN_PORT:-55432}}"

# Both write to stderr so they stay visible when a helper runs inside $(...) (wait_api, wait_update_stage).
log() { printf '[smoke %s] %s\n' "$(date -u +%H:%M:%S)" "$*" >&2; }
fail() { printf '::error::%s\n' "$*" >&2; return 1; }

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

# ---- In-place update helpers (local update API: GET /api/v1/update, antiforgery-protected POST /api/v1/update/approve) ----

# update_json: the node's update status as JSON (policy, installedVersion, stage, targetVersion, detail, lastError, approvalPending, targetDigest).
update_json() { curl -fsS --max-time 10 "$API/api/v1/update"; }

# wait_update_stage STAGE VERSION SECONDS: waits until the node reports STAGE for target VERSION, prints the final JSON.
wait_update_stage() {
    local stage="$1" version="$2" deadline=$((SECONDS + $3)) body=""
    while [ "$SECONDS" -lt "$deadline" ]; do
        body="$(update_json 2>/dev/null || true)"
        if [ -n "$body" ] && [ "$(echo "$body" | jq -r '.stage')" = "$stage" ] && [ "$(echo "$body" | jq -r '.targetVersion // empty')" = "$version" ]; then
            echo "$body"
            return 0
        fi
        sleep 3
    done
    log "last update status: ${body:-<none>}"
    fail "node did not report update stage $stage for $version within $3 s"
}

# approve_update: the local approval a person gives in the desktop app. The local API protects mutations with antiforgery
# (cookie from GET /api/csrf + matching X-CSRF-TOKEN header); presence verification is off by default on a fresh node.
approve_update() {
    local jar token code
    jar="$(mktemp)"
    token="$(curl -fsS --max-time 10 -c "$jar" "$API/api/csrf" | jq -r '.requestToken')"
    code="$(curl -sS --max-time 20 -o /dev/stderr -w '%{http_code}' -b "$jar" -H "X-CSRF-TOKEN: $token" \
        -H 'Content-Type: application/json' -d '{}' -X POST "$API/api/v1/update/approve")"
    rm -f "$jar"
    echo
    [ "$code" = "200" ] || fail "POST /api/v1/update/approve returned HTTP $code"
}

# dump_update_diagnostics: everything needed to debug a failed update (called from the failure trap and the workflow).
dump_update_diagnostics() {
    echo "::group::update diagnostics"
    systemctl status monitor-app monitor-app-update.path monitor-app-update.service --no-pager -l 2>&1 | tail -60 || true
    journalctl -u monitor-app --no-pager -n 200 2>&1 || true
    journalctl -u monitor-app-update --no-pager -n 100 2>&1 || true
    sudo ls -laR /var/lib/monitor-app/update /var/lib/monitor-app-helper 2>&1 | head -80 || true
    for f in update-state.json apply-request.json apply-result.json health.json; do
        echo "--- /var/lib/monitor-app/update/$f"; sudo cat "/var/lib/monitor-app/update/$f" 2>&1 || true; echo
    done
    echo "--- audit log"; sudo tail -40 /var/lib/monitor-app/update/update-audit.jsonl 2>&1 || true
    echo "--- retained pointer"; sudo cat /var/lib/monitor-app-helper/retained/last-known-good.json 2>&1 || true; echo
    echo "--- node.conf (active lines)"; sudo grep -Ev '^(#|$)' /etc/monitor-app/node.conf 2>&1 || true
    echo "--- update server log"; tail -40 "${SMOKE_UPDATE_SERVER_LOG:-/tmp/smoke-update/server.log}" 2>&1 || true
    dpkg -l monitor-app 2>&1 || true
    echo "::endgroup::"
}
