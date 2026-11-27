#!/bin/sh
# Automatic update of the Monitor App package (rpm or deb) on a Linux
# computer. Run as root by monitor-lite-update.timer, which the package
# installs: a few minutes after boot, then every 6 hours.
#
# It asks GitHub for the newest lite-v release, downloads that release's
# package for this system and its SHA256SUMS, refuses a file that does not
# match, and installs it with dnf or apt. The running collector notices
# that its program was replaced and restarts itself (agent.dart).
#
# Off when any signed-in user's shared Monitor folder has
# settings.json "update": {"auto": false}, like Windows and Android.
#   sh package-update.sh --check    # say what it would do, change nothing
set -u
repo=williebob555-lab/monitor-app
check=0
[ "${1:-}" = "--check" ] && check=1

if rpm -q monitor-app >/dev/null 2>&1; then
  kind=rpm; cur=$(rpm -q --qf '%{VERSION}' monitor-app)
elif dpkg-query -W monitor-app >/dev/null 2>&1; then
  kind=deb; cur=$(dpkg-query -W -f='${Version}' monitor-app)
else
  echo "monitor-app is not installed as a package"; exit 0
fi

for c in /home/*/.config/monitor-lite/config.json /root/.config/monitor-lite/config.json; do
  [ -f "$c" ] || continue
  folder=$(tr -d '\n' < "$c" | sed -n 's/.*"folder" *: *"\([^"]*\)".*/\1/p')
  [ -n "$folder" ] && [ -f "$folder/settings.json" ] || continue
  if tr -d ' \t\r\n' < "$folder/settings.json" | grep -q '"update":{[^}]*"auto":false'; then
    echo "automatic updates are off in $folder/settings.json"; exit 0
  fi
done

latest=$(curl -fsSL --max-time 30 -H 'User-Agent: monitor-lite' "https://api.github.com/repos/$repo/releases?per_page=30" \
  | grep -o '"tag_name": *"lite-v[0-9][0-9.]*"' | grep -o '[0-9][0-9.]*[0-9]' | sort -V | tail -1)
[ -n "$latest" ] || { echo "could not read the releases from GitHub"; exit 1; }
newest=$(printf '%s\n%s\n' "$cur" "$latest" | sort -V | tail -1)
if [ "$newest" = "$cur" ]; then echo "up to date ($cur)"; exit 0; fi

if [ "$kind" = rpm ]; then name="monitor-app-$latest-1.x86_64.rpm"; else name="monitor-app_${latest}_amd64.deb"; fi
echo "updating $cur -> $latest ($name)"
[ "$check" = 1 ] && exit 0

tmp=$(mktemp -d) || exit 1
trap 'rm -rf "$tmp"' EXIT
base="https://github.com/$repo/releases/download/lite-v$latest"
curl -fsSL --max-time 600 -o "$tmp/$name" "$base/$name" || { echo "download failed"; exit 1; }
curl -fsSL --max-time 30 -o "$tmp/SHA256SUMS" "$base/SHA256SUMS" || { echo "SHA256SUMS download failed"; exit 1; }
want=$(awk -v n="$name" '{f=$2; sub(/^\*/, "", f)} f==n {print tolower($1)}' "$tmp/SHA256SUMS")
got=$(sha256sum "$tmp/$name" | cut -d' ' -f1)
[ -n "$want" ] && [ "$want" = "$got" ] || { echo "$name does not match SHA256SUMS; not installed"; exit 1; }

if [ "$kind" = rpm ]; then
  dnf install -y "$tmp/$name"
else
  DEBIAN_FRONTEND=noninteractive apt-get install -y "$tmp/$name"
fi
