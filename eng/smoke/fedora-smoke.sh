#!/usr/bin/env bash
# rpm lifecycle smoke inside a fedora container WITHOUT systemd. Usage: fedora-smoke.sh OLD.rpm NEW.rpm
# Proves rpm install/upgrade/erase mechanics and that the packaged node runs as the service user and collects data.
# Does not prove systemd unit behavior or SELinux (see docs/TESTING.md).
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=eng/smoke/lib.sh
. "$here/lib.sh"
old_rpm="$1"
new_rpm="$2"
data=/var/lib/monitor-app
node_pid=""

dump() {
    echo "::group::diagnostics"
    find "$data" /opt/monitor-app -maxdepth 1 -ls 2>&1 | head -50 || true
    tail -n 100 /tmp/node.log 2>&1 || true
    echo "::endgroup::"
}
on_exit() {
    local rc=$?
    if [ "$rc" -ne 0 ]; then dump; fi
}
trap on_exit EXIT

start_node() {
    # Same environment the unit provides: state dir, default ports, unprivileged user.
    runuser -u monitor-app -- env MONITOR_APP_DATA_DIR="$data" /opt/monitor-app/MonitorApp.Peer.Node >>/tmp/node.log 2>&1 &
    node_pid=$!
}
stop_node() {
    if [ -n "$node_pid" ]; then
        kill "$node_pid" 2>/dev/null || true
        wait "$node_pid" 2>/dev/null || true
        node_pid=""
    fi
}

dnf install -y jq libicu openssl-libs util-linux >/dev/null

log "rpm -i old package"
rpm -i "$old_rpm"
rpm -V monitor-app || log "rpm -V reported differences (informational)"
test -x /opt/monitor-app/MonitorApp.Peer.Node || fail "node binary missing"
for f in /usr/lib/systemd/system/monitor-app.service /usr/lib/systemd/system/monitor-app-update.path /usr/lib/sysusers.d/monitor-app.conf /etc/monitor-app/node.conf /etc/monitor-app/update-key.json /usr/libexec/monitor-app/seed-retained-package.sh; do
    test -f "$f" || fail "missing $f"
done
getent passwd monitor-app >/dev/null || fail "monitor-app user missing"
[ "$(stat -c '%a %U' "$data")" = "700 monitor-app" ] || fail "data dir must be 0700 monitor-app"
grep -q '^User=monitor-app' /usr/lib/systemd/system/monitor-app.service || fail "unit does not run as monitor-app"

log "run the node as the service user: one collection + API check"
start_node
peer1="$(wait_api 60)"
wait_readings 90
seq1="$(max_sequence)"
test -f "$data/identity.p256" || fail "identity key missing"
id_sum1="$(sha256sum "$data/identity.p256" | cut -d' ' -f1)"
log "peer $peer1 sequence $seq1; process owner: $(ps -o user= -p "$node_pid" || true)"
stop_node

log "rpm -U newer package keeps identity and data"
rpm -U "$new_rpm"
log "installed: $(rpm -q monitor-app)"
test -f "$data/identity.p256" || fail "identity lost on upgrade"
[ "$id_sum1" = "$(sha256sum "$data/identity.p256" | cut -d' ' -f1)" ] || fail "identity key changed on upgrade"
start_node
peer2="$(wait_api 60)"
[ "$peer1" = "$peer2" ] || fail "peer id changed on upgrade: $peer1 -> $peer2"
wait_readings 90
[ "$(max_sequence)" -ge "$seq1" ] || fail "history did not survive upgrade"
stop_node

log "rpm -e keeps data and user (spec comment: removal retains them)"
rpm -e monitor-app
test ! -e /opt/monitor-app/MonitorApp.Peer.Node || fail "binary still present after erase"
test -f "$data/identity.p256" || fail "identity removed by erase"
getent passwd monitor-app >/dev/null || fail "user removed by erase"
log "fedora rpm smoke passed"
