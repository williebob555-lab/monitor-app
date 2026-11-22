#!/bin/sh
# Best-effort seeding of the retained last-known-good package so the first update has a rollback target.
# Usage: seed-retained-package.sh deb|rpm VERSION   (called from postinst / %post)
# The package manager does not hand maintainer scripts the path of the package being installed, so look at
# the parent process command lines (dpkg -i X.deb / rpm -U X.rpm / apt/dnf helpers) for a matching file, then
# at the package-manager cache. If nothing matches, the first rollback is reported as "no last-known-good
# package retained" and the next healthy update seeds it. Never fails the install.
kind="$1"
version="$2"
dest=/var/lib/monitor-app-helper/retained
[ -n "$kind" ] && [ -n "$version" ] || exit 0
# Only seed once: during an update the helper installs an unproven package, which must not replace last-known-good.
[ -e "$dest/last-known-good.json" ] && exit 0

matches() {
    f="$1"
    [ -f "$f" ] || return 1
    case "$kind" in
        deb) [ "$(dpkg-deb -f "$f" Package 2>/dev/null)" = monitor-app ] && [ "$(dpkg-deb -f "$f" Version 2>/dev/null)" = "$version" ] ;;
        rpm) [ "$(rpm -qp --qf '%{NAME} %{VERSION}' "$f" 2>/dev/null)" = "monitor-app $version" ] ;;
    esac
}

found=""
pid=$$
depth=0
while [ -z "$found" ] && [ "$pid" -gt 1 ] && [ "$depth" -lt 8 ]; do
    if [ -r "/proc/$pid/cmdline" ]; then
        for arg in $(tr '\0' '\n' < "/proc/$pid/cmdline" 2>/dev/null | grep -E "\.$kind\$" || true); do
            if matches "$arg"; then found="$arg"; break; fi
        done
    fi
    pid="$(awk '{print $4}' "/proc/$pid/stat" 2>/dev/null || echo 1)"
    depth=$((depth + 1))
done
if [ -z "$found" ]; then
    for f in /var/cache/apt/archives/monitor-app_*.deb /var/cache/libdnf5/*/packages/monitor-app-*.rpm /var/cache/dnf/*/packages/monitor-app-*.rpm; do
        if matches "$f"; then found="$f"; break; fi
    done
fi
[ -n "$found" ] || { echo "monitor-app: no copy of this package found to retain for rollback (non-fatal)." >&2; exit 0; }

mkdir -p "$dest" && chmod 0700 /var/lib/monitor-app-helper "$dest" || exit 0
name="lkg-$version.pkg"
cp "$found" "$dest/$name.tmp" && mv "$dest/$name.tmp" "$dest/$name" || exit 0
printf '{"Version":"%s","File":"%s"}' "$version" "$name" > "$dest/last-known-good.json.tmp" \
    && mv "$dest/last-known-good.json.tmp" "$dest/last-known-good.json"
for other in "$dest"/lkg-*.pkg; do
    [ "$other" = "$dest/$name" ] || rm -f "$other"
done
exit 0
