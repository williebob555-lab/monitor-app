#!/bin/sh
# Shares what UPower knows about a USB UPS with the Monitor App collector in
# Docker, as one small read-only file: no D-Bus, no USB device in the
# container. Steps: "UPS through UPower" in packaging/docker/README.md.
#
#   sh upower-export.sh --check                         what it would export; changes nothing
#   sudo sh upower-export.sh --install --dir DIR --user USER
#                                                       timer writing DIR/ups.txt every minute
#   sudo sh upower-export.sh --remove                   removes the timer (DIR is left alone)
#   upower-export.sh FILE                               one export (what the timer runs)
#
# It only reads from UPower (`upower -e`, `upower -i`), as USER, never root.
# It never sends anything to the UPS or UPower, and has no part in shutdown:
# UPower's own low-battery action stays exactly as configured.
set -eu

lib=/usr/local/lib/monitor-app
name=monitor-upower-export

ups() { upower -e 2>/dev/null | grep '/ups_' || true; }

# UPower's policy as the host has it (read-only): when it acts and how.
policy() {
  for f in /etc/UPower/UPower.conf /etc/UPower/UPower.conf.d/*.conf; do
    [ -r "$f" ] || continue
    sed -n 's/^[[:space:]]*\(UsePercentageForPolicy\|PercentageLow\|PercentageCritical\|PercentageAction\|TimeLow\|TimeCritical\|TimeAction\|CriticalPowerAction\)[[:space:]]*=[[:space:]]*\([^[:space:]]*\).*/config.\1: \2/p' "$f"
  done
}

# Every UPS UPower knows (never a laptop battery, mouse or keyboard), the
# daemon's own block (version, on-battery, critical-action) and its policy.
export_to() {
  out=$1
  tmp="$(dirname "$out")/.$(basename "$out").tmp"
  {
    echo "exported: $(date +%s)"
    for dev in $(ups); do
      echo "Device: $dev"
      upower -i "$dev" | sed '/^[[:space:]]*\(History\|Statistics\)/,$d'
    done
    upower -d 2>/dev/null | sed -n '/^Daemon:/,/^$/p'
    policy
  } > "$tmp"
  chmod 0644 "$tmp"
  mv -f "$tmp" "$out"
}

dir= user= mode=
while [ $# -gt 0 ]; do
  case $1 in
    --check) mode=check ;;
    --install) mode=install ;;
    --remove) mode=remove ;;
    --dir) dir=$2; shift ;;
    --user) user=$2; shift ;;
    -*) echo "unknown option $1" >&2; exit 2 ;;
    *) export_to "$1"; exit 0 ;;
  esac
  shift
done

command -v upower >/dev/null || { echo "upower is not installed on this host" >&2; exit 2; }

case $mode in
  check)
    devs=$(ups)
    if [ -z "$devs" ]; then echo "UPower sees no UPS here (laptop batteries and mice are never exported)."; exit 1; fi
    for dev in $devs; do
      echo "UPower UPS: $dev"
      upower -i "$dev" | grep -E 'native-path|vendor|model|serial|state|percentage|time to empty|warning-level' || true
    done
    upower -d 2>/dev/null | sed -n '/^Daemon:/,/^$/p' | grep -E 'daemon-version|on-battery|critical-action' || true
    policy
    ;;
  install)
    [ "$(id -u)" = 0 ] || { echo "run --install with sudo" >&2; exit 2; }
    [ -n "$dir" ] && [ -n "$user" ] || { echo "--install needs --dir and --user" >&2; exit 2; }
    id "$user" >/dev/null 2>&1 || { echo "no user $user" >&2; exit 2; }
    mkdir -p "$dir" && chown "$user": "$dir" && chmod 0755 "$dir"
    dir=$(cd "$dir" && pwd)
    mkdir -p "$lib"
    install -m 0755 "$0" "$lib/upower-export.sh"
    cat > "/etc/systemd/system/$name.service" <<UNIT
[Unit]
Description=Share UPower's UPS details with Monitor App (read-only file)
After=upower.service

[Service]
Type=oneshot
User=$user
ExecStart=/bin/sh $lib/upower-export.sh $dir/ups.txt
NoNewPrivileges=yes
ProtectSystem=strict
ProtectHome=read-only
ReadWritePaths=$dir
PrivateTmp=yes
PrivateDevices=yes
RestrictAddressFamilies=AF_UNIX
CapabilityBoundingSet=
UNIT
    cat > "/etc/systemd/system/$name.timer" <<UNIT
[Unit]
Description=Share UPower's UPS details with Monitor App every minute

[Timer]
OnBootSec=30s
OnUnitActiveSec=60s
AccuracySec=5s

[Install]
WantedBy=timers.target
UNIT
    systemctl daemon-reload
    systemctl enable --now "$name.timer"
    systemctl start "$name.service"
    echo "Installed $name.timer: $dir/ups.txt is rewritten every minute, as $user."
    head -n 1 "$dir/ups.txt"
    ;;
  remove)
    [ "$(id -u)" = 0 ] || { echo "run --remove with sudo" >&2; exit 2; }
    systemctl disable --now "$name.timer" 2>/dev/null || true
    rm -f "/etc/systemd/system/$name.timer" "/etc/systemd/system/$name.service" "$lib/upower-export.sh"
    systemctl daemon-reload
    echo "Removed $name.timer."
    ;;
  *) sed -n 2,14p "$0"; exit 2 ;;
esac
