#!/usr/bin/env bash
# Real-systemd lifecycle smoke for the Ubuntu .deb.
# Usage: ubuntu-smoke.sh OLD.deb NEW.deb PKGONLY.deb FIXTURE_DIR REPOSITORY
#   OLD.deb      0.9.1, installed first
#   NEW.deb      0.9.2, a genuine build (its node reports 0.9.2); offered to the node's own update agent
#   PKGONLY.deb  0.9.3 package whose payload is the 0.9.2 build: it installs, but its node reports 0.9.2, never 0.9.3, so
#                the update helper's health check for 0.9.3 fails and must roll back; later reused for a plain apt upgrade
#   FIXTURE_DIR  output of eng/smoke/update-fixture.sh (TLS test CA, signed manifests) for the versions above
#   REPOSITORY   owner/name baked into /etc/monitor-app/update-key.json
# Runs on a GitHub ubuntu runner (sudo, systemd as PID 1). Packaging decision (monitor-app.service / postinst):
# the unit is installed but NOT enabled or started on install; this script enables it explicitly.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=eng/smoke/lib.sh
. "$here/lib.sh"
old_deb="$1"
new_deb="$2"
pkgonly_deb="$3"
fixture="$4"
repo="$5"
data=/var/lib/monitor-app
serve=/tmp/smoke-update/serve
export SMOKE_UPDATE_SERVER_LOG=/tmp/smoke-update/server.log
server_pid=""

cleanup() {
    local rc=$?
    if [ "$rc" -ne 0 ]; then dump_update_diagnostics || true; fi
    if [ -n "$server_pid" ]; then kill "$server_pid" 2>/dev/null || true; fi
    sudo rm -f /usr/local/share/ca-certificates/monitor-app-smoke.crt
    sudo update-ca-certificates --fresh >/dev/null 2>&1 || true
}
trap cleanup EXIT

step() { echo "::group::$*"; log "$*"; }
endstep() { echo "::endgroup::"; }

step "install old package"
sudo apt-get install -y "$old_deb"
test -x /opt/monitor-app/MonitorApp.Peer.Node || fail "node binary missing"
test -f /usr/lib/systemd/system/monitor-app.service || fail "unit file missing"
getent passwd monitor-app >/dev/null || fail "monitor-app user missing"
state="$(systemctl is-enabled monitor-app 2>&1 || true)"
log "unit is-enabled after install: $state (packaging intends: disabled, not started)"
if systemctl is-active --quiet monitor-app; then log "unit is unexpectedly already active"; fi
endstep

step "enable and start the service"
sudo systemctl enable monitor-app
sudo systemctl start --no-block monitor-app
# systemd still enforces the unit's start timeout; this loop only watches, and snapshots a slow first start so a
# start that hangs can be diagnosed (which syscalls, sockets and files the node is waiting on).
snapshot_taken=0
for second in $(seq 1 150); do
    state="$(systemctl show monitor-app -p ActiveState --value)"
    [ "$state" = "activating" ] || break
    if [ "$second" -ge 30 ] && [ "$snapshot_taken" = 0 ]; then
        snapshot_taken=1
        pid="$(systemctl show monitor-app -p MainPID --value)"
        log "node still starting after ${second}s (pid $pid); snapshot follows"
        sudo ss -tanpu 2>/dev/null | grep "pid=$pid," || true
        sudo ls -l "/proc/$pid/fd" 2>/dev/null | sed 's/^.* -> //' | sort | uniq -c | sort -rn | head -40 || true
        for task in /proc/"$pid"/task/*; do
            printf '%s %s %s\n' "$(basename "$task")" "$(sudo cat "$task/comm" 2>/dev/null)" "$(sudo cat "$task/wchan" 2>/dev/null)"
        done | sort -k3 | uniq -c -f1 | head -40 || true
        if command -v strace >/dev/null 2>&1; then
            sudo timeout 5 strace -f -tt -p "$pid" -e trace=network,file,poll,epoll_wait 2>&1 | grep -v ENOENT | head -80 || true
        fi
        sudo journalctl -u monitor-app --no-pager -n 60 || true
    fi
    sleep 1
done
systemctl is-enabled --quiet monitor-app || fail "unit not enabled"
systemctl is-active --quiet monitor-app || fail "unit not active"
systemctl show monitor-app -p Type -p User -p MainPID -p ActiveState -p SubState
endstep

step "local API answers and real readings appear"
peer1="$(wait_api 60)"
log "peer id: $peer1"
wait_readings 90
seq1="$(max_sequence)"
log "max origin sequence: $seq1"
sudo test -f "$data/identity.p256" || fail "identity.p256 missing in $data"
id_sum1="$(sudo sha256sum "$data/identity.p256" | cut -d' ' -f1)"
sudo ls -la "$data"
[ "$(sudo stat -c '%a %U' "$data")" = "700 monitor-app" ] || fail "data dir must be 0700 monitor-app"
[ "$(sudo stat -c '%a' "$data/identity.p256")" = "600" ] || fail "identity key must be mode 0600"
ps -o user=,pid=,cmd= -C MonitorApp.Peer.Node
endstep

step "restart keeps identity"
sudo systemctl restart monitor-app
peer2="$(wait_api 60)"
[ "$peer1" = "$peer2" ] || fail "peer id changed on restart: $peer1 -> $peer2"
wait_readings 90
seq2="$(max_sequence)"
[ "$seq2" -ge "$seq1" ] || fail "origin sequence went backwards after restart ($seq1 -> $seq2)"
log "peer id stable ($peer2); sequence $seq1 -> $seq2"
endstep

# ---------------------------------------------------------------------------------------------------------------------
# In-place update through the node's OWN update agent and the root helper units (T034). Nothing in the node is relaxed:
# it still requires an HTTPS release API with a chain the system trusts, a manifest signed by the key in
# /etc/monitor-app/update-key.json (the key baked into the packages), and an explicit approval. The test CA is added to
# the system trust store (what an operator would do); the API base URL and poll interval are the documented
# MONITOR_APP_UPDATE_* settings in node.conf. A local HTTPS server plays the GitHub releases API + release downloads.
# ---------------------------------------------------------------------------------------------------------------------
old_ver="$(dpkg-deb -f "$old_deb" Version)"
new_ver="$(dpkg-deb -f "$new_deb" Version)"
bad_ver="$(dpkg-deb -f "$pkgonly_deb" Version)"
new_sha="$(sha256sum "$new_deb" | cut -d' ' -f1)"
helper_log() { sudo journalctl -u monitor-app-update --no-pager -o cat; }
installed_ver() { dpkg-query -W -f='${Version}' monitor-app 2>/dev/null || true; }

# publish_release VERSION: make that signed manifest + package the newest preview pre-release on the local server.
publish_release() {
    local v="$1"
    cp "$fixture/releases/$v/update-manifest.json" "$fixture/releases/$v/monitorapp-node-ubuntu-x64-$v.deb" "$serve/download/"
    jq -n --arg tag "preview-$v" '[{tag_name: $tag, draft: false, prerelease: true,
        assets: [{name: "update-manifest.json", browser_download_url: "https://127.0.0.1:8443/download/update-manifest.json"}]}]' \
        > "$serve/releases.json.tmp"
    mv "$serve/releases.json.tmp" "$serve/releases.json"
    log "published release $v on the local update server"
}

step "start the local HTTPS update server and trust its test CA"
mkdir -p "$serve/download"
sudo install -m 0644 "$fixture/tls/ca.crt" /usr/local/share/ca-certificates/monitor-app-smoke.crt
sudo update-ca-certificates
publish_release "$new_ver"
nohup python3 "$here/update-server.py" "$serve" "$fixture/tls/server.crt" "$fixture/tls/server.key" 8443 > "$SMOKE_UPDATE_SERVER_LOG" 2>&1 &
server_pid=$!
for _ in $(seq 1 20); do
    curl -fsS --max-time 3 "https://127.0.0.1:8443/repos/$repo/releases?per_page=20" >/dev/null 2>&1 && break
    sleep 1
done
curl -fsS --max-time 3 "https://127.0.0.1:8443/repos/$repo/releases?per_page=20" | jq -c '.[0].tag_name' || fail "update server not answering over trusted HTTPS"
# The trust store must be what makes it work: with an empty trust anchor the same request must fail.
if curl -fsS --max-time 3 --cacert /dev/null "https://127.0.0.1:8443/" >/dev/null 2>&1; then fail "server certificate verified without the test CA"; fi
systemctl is-enabled --quiet monitor-app-update.path || fail "monitor-app-update.path is not enabled"
systemctl is-active --quiet monitor-app-update.path || fail "monitor-app-update.path is not active"
endstep

step "point the node at the local release API, restart, and wait for $new_ver to be staged"
sudo tee -a /etc/monitor-app/node.conf >/dev/null <<EOF_CONF

# --- install smoke: local release API (the test CA is in the system trust store) ---
MONITOR_APP_UPDATE_API_URL=https://127.0.0.1:8443/
MONITOR_APP_UPDATE_INTERVAL_HOURS=0.01
MONITOR_APP_UPDATE_POLICY=approve
EOF_CONF
sudo systemctl restart monitor-app
peer_u0="$(wait_api 60)"
[ "$peer1" = "$peer_u0" ] || fail "peer id changed on config restart: $peer1 -> $peer_u0"
[ "$(update_json | jq -r '.installedVersion')" = "$old_ver" ] || fail "node does not report installed version $old_ver: $(update_json)"
status="$(wait_update_stage Staged "$new_ver" 180)"
echo "$status" | jq -c .
[ "$(echo "$status" | jq -r '.approvalPending')" = "true" ] || fail "staged update is not awaiting approval: $status"
[ "$(echo "$status" | jq -r '.targetDigest')" = "$new_sha" ] || fail "staged SHA-256 $(echo "$status" | jq -r '.targetDigest') != package SHA-256 $new_sha"
[ "$(echo "$status" | jq -r '.policy')" = "approve" ] || fail "policy is not approve (consent-first): $status"
[ "$(installed_ver)" = "$old_ver" ] || fail "package was installed before approval"
sudo find "$data/update/staging" -type f -exec sha256sum {} + | grep -c "$new_sha" >/dev/null || fail "verified package not found in the owner-only staging directory"
endstep

step "approve $new_ver and wait for the root helper to install it"
approve_update
for _ in $(seq 1 120); do
    [ "$(installed_ver)" = "$new_ver" ] && break
    sleep 3
done
[ "$(installed_ver)" = "$new_ver" ] || fail "dpkg still reports $(installed_ver) after approval"
peer_u1="$(wait_api 120)"
[ "$peer1" = "$peer_u1" ] || fail "peer id changed on in-place update: $peer1 -> $peer_u1"
status="$(wait_update_stage Applied "$new_ver" 60)"
echo "$status" | jq -c .
[ "$(echo "$status" | jq -r '.installedVersion')" = "$new_ver" ] || fail "node reports installed version $(echo "$status" | jq -r '.installedVersion'), expected $new_ver"
helper_log | grep -c "apply-update $new_ver: Applied" >/dev/null || { helper_log; fail "helper did not record Applied for $new_ver"; }
[ "$(sudo jq -r '.version' "$data/update/health.json")" = "$new_ver" ] || fail "health marker does not report $new_ver: $(sudo cat "$data/update/health.json")"
[ "$(sudo jq -r '.Version' /var/lib/monitor-app-helper/retained/last-known-good.json)" = "$new_ver" ] || fail "retained last-known-good package is not $new_ver"
systemctl is-enabled --quiet monitor-app || fail "unit lost its enabled state on in-place update"
systemctl is-active --quiet monitor-app || fail "service not active after in-place update"
[ "$id_sum1" = "$(sudo sha256sum "$data/identity.p256" | cut -d' ' -f1)" ] || fail "identity key file changed on in-place update"
wait_readings 90
seq_u1="$(max_sequence)"
[ "$seq_u1" -ge "$seq2" ] || fail "history did not survive the in-place update (sequence $seq2 -> $seq_u1)"
log "in-place update to $new_ver succeeded: peer id $peer_u1 stable, sequence $seq2 -> $seq_u1"
endstep

step "rollback: serve $bad_ver (installs, but its node reports the wrong version) and expect the helper to restore $new_ver"
publish_release "$bad_ver"
status="$(wait_update_stage Staged "$bad_ver" 240)"
echo "$status" | jq -c .
approve_update
# The helper waits 90 s for a healthy $bad_ver, then reinstalls the retained package and requires it to be healthy.
for _ in $(seq 1 150); do
    helper_log | grep -c "apply-update $bad_ver: " >/dev/null && break
    sleep 3
done
helper_log | grep "apply-update $bad_ver: " || fail "helper recorded no outcome for $bad_ver"
helper_log | grep -c "apply-update $bad_ver: RolledBack" >/dev/null || fail "helper outcome for $bad_ver was not RolledBack"
[ "$(installed_ver)" = "$new_ver" ] || fail "dpkg reports $(installed_ver) after rollback, expected $new_ver"
peer_u2="$(wait_api 120)"
[ "$peer1" = "$peer_u2" ] || fail "peer id changed across rollback: $peer1 -> $peer_u2"
status="$(wait_update_stage RolledBack "$bad_ver" 60)"
echo "$status" | jq -c .
[ "$(echo "$status" | jq -r '.installedVersion')" = "$new_ver" ] || fail "node does not run $new_ver after rollback: $status"
[ "$(sudo jq -r '.version' "$data/update/health.json")" = "$new_ver" ] || fail "health marker after rollback is not $new_ver"
systemctl is-active --quiet monitor-app || fail "service not active after rollback"
[ "$id_sum1" = "$(sudo sha256sum "$data/identity.p256" | cut -d' ' -f1)" ] || fail "identity key file changed across rollback"
wait_readings 90
seq_u2="$(max_sequence)"
[ "$seq_u2" -ge "$seq_u1" ] || fail "history did not survive the rollback (sequence $seq_u1 -> $seq_u2)"
log "rollback verified: running $new_ver again, state RolledBack, peer id and data intact"
endstep

step "systemd-analyze security (informational)"
sudo systemd-analyze security monitor-app.service --no-pager || true
endstep

step "upgrade to the newer package (plain apt, no update agent)"
sudo apt-get install -y "$pkgonly_deb"
log "installed version: $(dpkg-query -W -f='${Version}' monitor-app)"
# prerm stops the unit on upgrade and postinst does not restart it (the update helper restarts it in the real flow).
if systemctl is-active --quiet monitor-app; then
    log "service active after upgrade"
else
    echo "::warning::service is not active after 'apt install' of a newer deb (prerm stops it; postinst does not restart it)"
    sudo systemctl start monitor-app
fi
systemctl is-enabled --quiet monitor-app || fail "unit lost its enabled state on upgrade"
peer3="$(wait_api 60)"
[ "$peer1" = "$peer3" ] || fail "peer id changed on upgrade: $peer1 -> $peer3"
id_sum3="$(sudo sha256sum "$data/identity.p256" | cut -d' ' -f1)"
[ "$id_sum1" = "$id_sum3" ] || fail "identity key file changed on upgrade"
wait_readings 90
seq3="$(max_sequence)"
[ "$seq3" -ge "$seq1" ] || fail "history did not survive upgrade (sequence $seq1 -> $seq3)"
log "upgrade kept identity ($peer3) and data (sequence $seq1 -> $seq3)"
endstep

step "apt remove keeps data"
sudo apt-get remove -y monitor-app
test ! -e /opt/monitor-app/MonitorApp.Peer.Node || fail "binary still present after remove"
sudo test -f "$data/identity.p256" || fail "identity removed by 'apt remove'"
getent passwd monitor-app >/dev/null || fail "user removed by 'apt remove'"
if systemctl is-active --quiet monitor-app; then fail "service still active after remove"; fi
sudo ls -la "$data"
endstep

step "apt purge removes data"
sudo apt-get purge -y monitor-app
test ! -e "$data" || fail "$data still present after purge"
test ! -e /etc/monitor-app || fail "/etc/monitor-app still present after purge"
if getent passwd monitor-app >/dev/null; then fail "monitor-app user still present after purge"; fi
endstep

log "ubuntu lifecycle smoke passed"
