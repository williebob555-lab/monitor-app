#!/bin/sh
# Keeps one Docker deployment of the Monitor App collector on the newest
# release, so nobody has to touch the server.
#
#   sudo sh update.sh --install --dir /path/to/compose/project   # once
#   sudo sh update.sh --dir /path/to/compose/project             # update now
#   sudo sh update.sh --dir /path/to/compose/project --check     # show, change nothing
#
# Everything is read from that project's own Compose configuration: the
# service, its image name, the host folder mounted at /monitor (the shared
# Monitor folder) and at /data (this install's private state, holding the
# device id). Flags override any of them: --service --image --folder --state
# --id --project. It only ever builds and restarts that one service of that
# one project (--no-deps); no other container is touched.
#
# It runs as root from a systemd unit that --install creates for this project
# only, so your user needs no docker group. --install copies this script to
# /usr/local/lib/monitor-app/ (root-owned) so the nightly job cannot be
# changed without root.
#
# Downloads are checked by the Dockerfile against the release's SHA256SUMS.
# Every attempt is appended to this device's log.txt in the Monitor folder.
set -u

dir='' service='' image='' folder='' state='' id='' project='' install=0 check=0
while [ $# -gt 0 ]; do
  case "$1" in
    --dir) dir=$2; shift ;;
    --service) service=$2; shift ;;
    --image) image=$2; shift ;;
    --folder) folder=$2; shift ;;
    --state) state=$2; shift ;;
    --id) id=$2; shift ;;
    --project) project=$2; shift ;;
    --install) install=1 ;;
    --check) check=1 ;;
    *) echo "unknown option: $1" >&2; exit 2 ;;
  esac
  shift
done
[ -n "$dir" ] || { echo "say which deployment: --dir /path/to/its/compose/project" >&2; exit 2; }
dir=$(cd "$dir" && pwd) || exit 2
[ "$(id -u)" = 0 ] || { echo "run with sudo (Docker is used as root, only for this project)" >&2; exit 2; }

dc() { if [ -n "$project" ]; then docker compose --project-directory "$dir" -p "$project" "$@"; else docker compose --project-directory "$dir" "$@"; fi; }

# The collector service: the one built from this Dockerfile (or --service).
if [ -z "$service" ]; then
  services=$(dc config --services) || { echo "no Compose project in $dir" >&2; exit 2; }
  if [ "$(echo "$services" | wc -l)" -ne 1 ]; then
    echo "several services in $dir ($(echo $services)); say which with --service" >&2; exit 2
  fi
  service=$services
fi
cfg=$(dc config "$service") || exit 2
# Host folders mounted at a container path, from the normalised config.
mount_of() { echo "$cfg" | awk -v t="$1" '/source:/{s=$2} /target:/{if($2==t){print s; exit}}'; }
[ -n "$image" ] || image=$(echo "$cfg" | awk '/^ *image:/{print $2; exit}')
[ -n "$folder" ] || folder=$(mount_of /monitor)
[ -n "$state" ] || state=$(mount_of /data)
[ -n "$id" ] || id=$(grep -o '"id": *"[^"]*"' "$state/.config/monitor-lite/config.json" 2>/dev/null | sed 's/.*"\([^"]*\)"$/\1/')
pin=$(dc config "$service" | awk '/VERSION:/{print $2; exit}' | tr -d '"')

log() {
  echo "$1"
  f="$folder/devices/$id/log.txt"
  # Append only to the collector's existing log, so ownership stays yours.
  [ -n "$id" ] && [ -f "$f" ] && echo "$(date +%Y-%m-%dT%H:%M:%S) $1" >> "$f"
}

if [ $check = 1 ]; then
  echo "project dir: $dir"; echo "service:     $service"; echo "image:       $image"
  echo "Monitor folder (/monitor): ${folder:-NOT FOUND}"; echo "state (/data): ${state:-NOT FOUND}"
  echo "device id:   ${id:-NOT FOUND}"; echo "log file:    $folder/devices/$id/log.txt $( [ -f "$folder/devices/$id/log.txt" ] && echo '(found)' || echo '(MISSING: updates will not be logged)')"
  echo "running:     $(docker run --rm --entrypoint /usr/local/bin/monitor-agent "$image" --version 2>/dev/null || echo unknown)"
  exit 0
fi

if [ $install = 1 ]; then
  name=$(basename "$dir" | tr -c 'a-zA-Z0-9_-\n' '-')
  install -d -m 0755 /usr/local/lib/monitor-app
  install -m 0755 -o root -g root "$0" /usr/local/lib/monitor-app/update.sh
  args="--dir $dir --service $service"
  [ -n "$project" ] && args="$args --project $project"
  for o in image folder state id; do eval "v=\$$o"; [ -n "$v" ] && args="$args --$o $v"; done
  cat > /etc/systemd/system/monitor-update-$name.service <<UNIT
[Unit]
Description=Update the Monitor App collector in $dir
After=docker.service network-online.target
Wants=network-online.target
[Service]
Type=oneshot
ExecStart=/bin/sh /usr/local/lib/monitor-app/update.sh $args
UNIT
  cat > /etc/systemd/system/monitor-update-$name.timer <<UNIT
[Unit]
Description=Daily check for a new Monitor App release ($dir)
[Timer]
OnCalendar=*-*-* 04:20
RandomizedDelaySec=30m
Persistent=true
[Install]
WantedBy=timers.target
UNIT
  systemctl daemon-reload
  systemctl enable --now "monitor-update-$name.timer"
  echo "Installed monitor-update-$name.timer for $dir ($service, $image). Next run:"
  systemctl list-timers "monitor-update-$name.timer" --no-pager | sed -n 2p
  exit 0
fi

if [ -n "$pin" ] && [ "$pin" != latest ]; then log "update: pinned to $pin, not checking"; exit 0; fi
new=$(curl -fsSL "https://api.github.com/repos/williebob555-lab/monitor-app/releases?per_page=30" \
  | grep -o '"tag_name": *"lite-v[^"]*"' | head -n1 | sed 's/.*lite-v//; s/"$//')
if [ -z "$new" ]; then log "update: could not read the newest release (offline?), trying again tomorrow"; exit 0; fi
have=$(docker run --rm --entrypoint /usr/local/bin/monitor-agent "$image" --version 2>/dev/null | grep -o '[0-9][0-9.]*' | head -n1)
[ "$have" = "$new" ] && exit 0
log "update: ${have:-unknown} -> $new, rebuilding"
out=$(mktemp)
if dc build --pull --no-cache --build-arg VERSION="$new" "$service" >"$out" 2>&1 \
   && dc up -d --no-deps "$service" >>"$out" 2>&1; then
  log "update: now running $new"
else
  log "ERROR update to $new failed, still on ${have:-unknown}: $(tail -n 3 "$out" | tr '\n' ' ')"
  rm -f "$out"; exit 1
fi
rm -f "$out"
