#!/bin/sh
# Keeps the Docker collector on the newest Monitor App release, so nobody has
# to touch this server. Run once with --install: it sets up a daily systemd
# timer (as you, not root) that runs this script. Each run compares the
# running version with the newest lite-v release and, if newer, rebuilds the
# image (the Dockerfile checks the SHA256 before using it) and restarts.
# Every attempt is written to this device's log.txt in the Monitor folder.
set -u
here=$(cd "$(dirname "$0")" && pwd)
cd "$here"

if [ "${1:-}" = --install ]; then
  mkdir -p ~/.config/systemd/user
  cat > ~/.config/systemd/user/monitor-update.service <<UNIT
[Unit]
Description=Update the Monitor App collector
[Service]
Type=oneshot
ExecStart=/bin/sh $here/update.sh
UNIT
  cat > ~/.config/systemd/user/monitor-update.timer <<UNIT
[Unit]
Description=Daily check for a new Monitor App release
[Timer]
OnCalendar=*-*-* 04:20
RandomizedDelaySec=30m
Persistent=true
[Install]
WantedBy=timers.target
UNIT
  systemctl --user daemon-reload
  systemctl --user enable --now monitor-update.timer
  # Run the timer even when nobody is signed in (servers).
  loginctl show-user "$(id -un)" -p Linger | grep -q yes || sudo loginctl enable-linger "$(id -un)"
  echo "Daily update check on. Next run:"; systemctl --user list-timers monitor-update.timer --no-pager | sed -n 2p
  exit 0
fi

folder=$(sed -n 's/^MONITOR_FOLDER=//p' .env | tail -n1)
pin=$(sed -n 's/^VERSION=//p' .env | tail -n1)
id=$(grep -o '"id": *"[^"]*"' data/.config/monitor-lite/config.json 2>/dev/null | sed 's/.*"\([^"]*\)"$/\1/')
log() {
  echo "$1"
  [ -n "$id" ] && [ -d "$folder/devices/$id" ] && echo "$(date +%Y-%m-%dT%H:%M:%S) $1" >> "$folder/devices/$id/log.txt"
}

if [ -n "$pin" ] && [ "$pin" != latest ]; then log "update: pinned to $pin in .env, not checking"; exit 0; fi
new=$(curl -fsSL "https://api.github.com/repos/williebob555-lab/monitor-app/releases?per_page=30" \
  | grep -o '"tag_name": *"lite-v[^"]*"' | head -n1 | sed 's/.*lite-v//; s/"$//')
if [ -z "$new" ]; then log "update: could not read the newest release (offline?), trying again tomorrow"; exit 0; fi
have=$(docker run --rm --entrypoint /usr/local/bin/monitor-agent monitor-app-collector --version 2>/dev/null | grep -o '[0-9][0-9.]*' | head -n1)
if [ "$have" = "$new" ]; then exit 0; fi
log "update: $have -> $new, rebuilding"
if docker compose build --pull --no-cache --build-arg VERSION="$new" >/tmp/monitor-update.log 2>&1 && docker compose up -d >>/tmp/monitor-update.log 2>&1; then
  log "update: now running $new"
else
  log "ERROR update to $new failed, still on $have: $(tail -n 3 /tmp/monitor-update.log | tr '\n' ' ')"
  exit 1
fi
