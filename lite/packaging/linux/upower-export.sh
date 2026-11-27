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
# It only reads from UPower (`upower -e`, `-i`, `-d`) and its policy files,
# as USER, never root, and never changes either.
# It never sends anything to the UPS or UPower, and has no part in shutdown:
# UPower's own low-battery action stays exactly as configured.
set -eu

lib=/usr/local/lib/monitor-app
conf=${UPOWER_CONF_DIR:-/etc/UPower}   # overridable for tests only
name=monitor-upower-export

# Plain C output whatever the host's language: "41.0 minutes", "100%",
# English state words and dot decimals, so the collector parses the same text
# on every host.
export LC_ALL=C LANG=C

# A hung UPower never holds the timer: each call gets 10 s.
up() { if command -v timeout >/dev/null; then timeout 10 upower "$@"; else upower "$@"; fi; }

ups() { up -e 2>/dev/null | grep '/ups_' || true; }

# UPower's policy as the host has it (read-only). UPower reads UPower.conf,
# then UPower.conf.d/*.conf in name order, a later file's key winning; only
# keys inside the [UPower] group count. Each file that sets a key is named
# ("policy-file:") before its keys, in that same order, so the reader ends
# up with the effective values.
policy() {
  for f in "$conf/UPower.conf" "$conf"/UPower.conf.d/*.conf; do
    [ -r "$f" ] || continue
    awk -v file="$f" '
      /^[[:space:]]*\[/ { g = $0; gsub(/[][[:space:]]/, "", g); next }
      g == "UPower" && /^[[:space:]]*(UsePercentageForPolicy|PercentageLow|PercentageCritical|PercentageAction|TimeLow|TimeCritical|TimeAction|CriticalPowerAction)[[:space:]]*=/ {
        k = $0; sub(/^[[:space:]]*/, "", k); sub(/[[:space:]]*=.*/, "", k)
        v = $0; sub(/^[^=]*=[[:space:]]*/, "", v); sub(/[[:space:]#;].*$/, "", v)
        if (!named) { print "policy-file: " file; named = 1 }
        print "config." k ": " v
      }' "$f"
  done
}

# Every UPS UPower knows (never a laptop battery, mouse or keyboard), the
# daemon's own block (version, on-battery, critical-action) and its policy.
# Written whole to a temporary file next to FILE and renamed over it, so a
# reader never sees half a file. When UPower doesn't answer, the file says
# so ("error:") instead of keeping old numbers. The folder is never created
# here: if its storage isn't mounted, nothing is written.
export_to() {
  out=$1
  d=$(dirname "$out")
  [ -d "$d" ] || { echo "$d does not exist (is its storage mounted?); nothing written" >&2; exit 1; }
  tmp=$(mktemp "$d/.$(basename "$out").XXXXXX")
  trap 'rm -f "$tmp"' EXIT
  {
    echo "exported: $(date +%s)"
    if ! list=$(up -e 2>/dev/null); then
      echo "error: UPower did not answer (is upower.service running?)"
    else
      for dev in $(printf '%s\n' "$list" | grep '/ups_' || true); do
        if info=$(up -i "$dev" 2>/dev/null); then
          echo "Device: $dev"
          printf '%s\n' "$info" | sed '/^[[:space:]]*\(History\|Statistics\)/,$d'
        else
          echo "error: UPower did not answer for $dev"
        fi
      done
      up -d 2>/dev/null | sed -n '/^Daemon:/,/^$/p'
    fi
    policy
  } > "$tmp"
  chmod 0644 "$tmp"
  sync "$tmp" 2>/dev/null || true
  mv -f "$tmp" "$out"
  trap - EXIT
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
      up -i "$dev" | grep -E 'native-path|vendor|model|serial|state|percentage|time to empty|warning-level' || true
    done
    up -d 2>/dev/null | sed -n '/^Daemon:/,/^$/p' | grep -E 'daemon-version|on-battery|critical-action' || true
    echo "Policy, in the order UPower applies it (later lines win):"
    policy
    ;;
  install)
    [ "$(id -u)" = 0 ] || { echo "run --install with sudo" >&2; exit 2; }
    [ -n "$dir" ] && [ -n "$user" ] || { echo "--install needs --dir and --user" >&2; exit 2; }
    id "$user" >/dev/null 2>&1 || { echo "no user $user" >&2; exit 2; }
    [ -d "$(dirname "$dir")" ] || { echo "$(dirname "$dir") does not exist (is its storage mounted?)" >&2; exit 2; }
    mkdir -p "$dir" && chown "$user": "$dir" && chmod 0755 "$dir"
    dir=$(cd "$dir" && pwd)
    mkdir -p "$lib"
    install -m 0755 "$0" "$lib/upower-export.sh"
    cat > "/etc/systemd/system/$name.service" <<UNIT
[Unit]
Description=Share UPower's UPS details with Monitor App (read-only file)
After=upower.service
# Never write to the bare mount point while the storage is missing.
RequiresMountsFor=$dir

[Service]
Type=oneshot
User=$user
Environment=LC_ALL=C
TimeoutStartSec=45
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
    findmnt -no SOURCE,TARGET -T "$dir" 2>/dev/null | sed 's/^/On storage: /' || true
    command -v ls >/dev/null && ls -Zd "$dir" 2>/dev/null | sed 's/^/SELinux label: /' || true
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
