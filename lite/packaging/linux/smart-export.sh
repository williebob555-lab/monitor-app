#!/bin/sh
# Shares each disk's health (SMART) with the Monitor App collector, as one
# small read-only file per disk: the collector never gets the disks, root or
# any capability. Roadmap row 38, design in docs/COMPANION.md. Steps:
# "Disk health (SMART)" in packaging/docker/README.md.
#
#   sudo sh smart-export.sh --check                     what it would export; changes nothing
#   sudo sh smart-export.sh --install --dir DIR         timer writing DIR/smart-<disk>.json every 30 min
#   sudo sh smart-export.sh --remove                    removes the timer (DIR is left alone)
#   smart-export.sh DIR                                 one export (what the timer runs)
#
# It only asks smartctl to READ (identity, health, attributes); it never runs
# a self-test, changes a setting or writes to a disk, and it never wakes a
# sleeping disk (-n standby). Reading SMART needs root; the unit keeps only
# the two capabilities smartctl uses, sees only disk devices, read-only, and
# can write only DIR. Needs smartmontools on the host.
set -eu

lib=/usr/local/lib/monitor-app
name=monitor-smart-export
export LC_ALL=C LANG=C

sc() { if command -v timeout >/dev/null; then timeout 30 smartctl "$@"; else smartctl "$@"; fi; }

# Every disk smartctl finds: "/dev/sda -d sat", "/dev/nvme0 -d nvme".
disks() { sc --scan 2>/dev/null | sed 's/[[:space:]]*#.*//' | awk 'NF { print $1 " " $2 " " $3 }'; }

export_to() {
  d=$1
  [ -d "$d" ] || { echo "$d does not exist (is its storage mounted?); nothing written" >&2; exit 1; }
  now=$(date +%s)
  if ! command -v smartctl >/dev/null; then
    tmp=$(mktemp "$d/.smart-host.json.XXXXXX")
    printf '{"exported": %s, "device": "host", "error": "smartctl is not installed on this host (smartmontools)"}\n' "$now" > "$tmp"
    chmod 0644 "$tmp"; mv -f "$tmp" "$d/smart-host.json"; exit 0
  fi
  disks | while read -r dev dflag dtype; do
    base=$(basename "$dev")
    out="$d/smart-$base.json"
    tmp=$(mktemp "$d/.smart-$base.json.XXXXXX")
    # smartctl's exit status is a bit mask (2 = left asleep, 4+ = findings);
    # its JSON says which, so the status is not an error here.
    json=$(sc -j -i -H -A -n standby $dflag $dtype "$dev" 2>/dev/null || true)
    if [ -n "$json" ]; then
      printf '{"exported": %s, "device": "%s", "smart": %s}\n' "$now" "$dev" "$json" > "$tmp"
    else
      printf '{"exported": %s, "device": "%s", "error": "smartctl gave no answer for %s"}\n' "$now" "$dev" "$dev" > "$tmp"
    fi
    chmod 0644 "$tmp"
    mv -f "$tmp" "$out"
  done
  rm -f "$d/smart-host.json"
}

dir= mode=
while [ $# -gt 0 ]; do
  case $1 in
    --check) mode=check ;;
    --install) mode=install ;;
    --remove) mode=remove ;;
    --dir) dir=$2; shift ;;
    -*) echo "unknown option $1" >&2; exit 2 ;;
    *) export_to "$1"; exit 0 ;;
  esac
  shift
done

case $mode in
  check)
    command -v smartctl >/dev/null || { echo "smartctl is not installed: sudo dnf install smartmontools (Fedora) or sudo apt install smartmontools (Ubuntu)"; exit 1; }
    [ "$(id -u)" = 0 ] || { echo "run --check with sudo (reading SMART needs root)" >&2; exit 2; }
    disks | while read -r dev dflag dtype; do
      echo "== $dev"
      sc -i -H -n standby $dflag $dtype "$dev" 2>/dev/null | grep -E 'Model|Serial|overall-health|Device is in' || true
    done
    ;;
  install)
    [ "$(id -u)" = 0 ] || { echo "run --install with sudo" >&2; exit 2; }
    [ -n "$dir" ] || { echo "--install needs --dir" >&2; exit 2; }
    command -v smartctl >/dev/null || { echo "install smartmontools first" >&2; exit 2; }
    [ -d "$(dirname "$dir")" ] || { echo "$(dirname "$dir") does not exist (is its storage mounted?)" >&2; exit 2; }
    mkdir -p "$dir" && chmod 0755 "$dir"
    dir=$(cd "$dir" && pwd)
    mkdir -p "$lib"
    install -m 0755 "$0" "$lib/smart-export.sh"
    cat > "/etc/systemd/system/$name.service" <<UNIT
[Unit]
Description=Share disk health (SMART) with Monitor App (read-only files)
RequiresMountsFor=$dir

[Service]
Type=oneshot
Environment=LC_ALL=C
TimeoutStartSec=5min
ExecStart=/bin/sh $lib/smart-export.sh $dir
# Root only for the SMART ioctls; everything else taken away.
CapabilityBoundingSet=CAP_SYS_RAWIO CAP_SYS_ADMIN
AmbientCapabilities=
NoNewPrivileges=yes
ProtectSystem=strict
ProtectHome=yes
ReadWritePaths=$dir
PrivateTmp=yes
PrivateNetwork=yes
RestrictAddressFamilies=AF_UNIX
DevicePolicy=closed
DeviceAllow=block-sd r
DeviceAllow=block-blkext r
DeviceAllow=char-nvme r
ProtectKernelTunables=yes
ProtectKernelModules=yes
ProtectControlGroups=yes
UNIT
    cat > "/etc/systemd/system/$name.timer" <<UNIT
[Unit]
Description=Share disk health with Monitor App every 30 minutes

[Timer]
OnBootSec=2min
OnUnitActiveSec=30min
AccuracySec=1min

[Install]
WantedBy=timers.target
UNIT
    systemctl daemon-reload
    systemctl enable --now "$name.timer"
    systemctl start "$name.service"
    echo "Installed $name.timer: $dir/smart-<disk>.json are rewritten every 30 minutes."
    ls -l "$dir"
    ls -Zd "$dir" 2>/dev/null | sed 's/^/SELinux label: /' || true
    ;;
  remove)
    [ "$(id -u)" = 0 ] || { echo "run --remove with sudo" >&2; exit 2; }
    systemctl disable --now "$name.timer" 2>/dev/null || true
    rm -f "/etc/systemd/system/$name.timer" "/etc/systemd/system/$name.service" "$lib/smart-export.sh"
    systemctl daemon-reload
    echo "Removed $name.timer."
    ;;
  *) sed -n 2,16p "$0"; exit 2 ;;
esac
