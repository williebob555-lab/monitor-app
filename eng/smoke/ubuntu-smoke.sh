#!/usr/bin/env bash
# Real-systemd lifecycle smoke for the Ubuntu .deb. Usage: ubuntu-smoke.sh OLD.deb NEW.deb
# Runs on a GitHub ubuntu runner (sudo, systemd as PID 1). Packaging decision (monitor-app.service / postinst):
# the unit is installed but NOT enabled or started on install; this script enables it explicitly.
set -euo pipefail
here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
# shellcheck source=eng/smoke/lib.sh
. "$here/lib.sh"
old_deb="$1"
new_deb="$2"
data=/var/lib/monitor-app

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
sudo systemctl enable --now monitor-app
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

step "systemd-analyze security (informational)"
sudo systemd-analyze security monitor-app.service --no-pager || true
endstep

step "upgrade to the newer package"
sudo apt-get install -y "$new_deb"
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
