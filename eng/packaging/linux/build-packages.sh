#!/usr/bin/env bash
# Builds a self-contained linux-x64 publish and packages it as a .deb (dpkg-deb) and, when
# rpmbuild is installed, an .rpm. Usage: eng/packaging/linux/build-packages.sh [version] [outdir]
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../../.." && pwd)"
version="${1:-0.0.0-preview}"
out="${2:-$root/artifacts/packages}"
# Optional non-secret trust anchor baked into the package: JSON {"keyId","publicKeySpkiBase64","repository"}.
update_key="${MONITOR_APP_UPDATE_KEY_JSON:-}"
rpm_version="${version//-/~}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$out"

stage="$work/stage"
mkdir -p "$stage/opt/monitor-app" "$stage/etc/monitor-app" "$stage/usr/lib/systemd/system" "$stage/usr/lib/sysusers.d"
if [ -n "${MONITOR_APP_FROM_STAGE:-}" ]; then
    # Reuse a stage tree built elsewhere (the preview workflow stages on Ubuntu and runs rpmbuild in a Fedora container).
    tar --no-same-owner -xzf "$MONITOR_APP_FROM_STAGE" -C "$stage"
else
    # Publish from a throwaway copy: a RID-specific restore rewrites every packages.lock.json it touches.
    src="$work/src"
    mkdir -p "$src"
    (cd "$root" && tar --exclude='bin' --exclude='obj' --exclude='artifacts' -cf - src contracts Directory.Build.props Directory.Packages.props global.json MonitorApp.sln) | tar -xf - -C "$src"
    dotnet publish "$src/src/MonitorApp.Peer.Node/MonitorApp.Peer.Node.csproj" \
        -c Release -r linux-x64 --self-contained -p:RestoreLockedMode=false -p:DebugType=None -p:DebugSymbols=false -p:Version="$version" -o "$stage/opt/monitor-app" >&2
    install -m 0644 "$here/monitor-app.service" "$stage/usr/lib/systemd/system/monitor-app.service"
    install -m 0644 "$here/monitor-app.sysusers" "$stage/usr/lib/sysusers.d/monitor-app.conf"
    install -m 0640 "$here/node.conf" "$stage/etc/monitor-app/node.conf"
    install -m 0644 "$here/monitor-app-update.service" "$here/monitor-app-update.path" "$stage/usr/lib/systemd/system/"
    mkdir -p "$stage/usr/libexec/monitor-app"
    install -m 0755 "$here/seed-retained-package.sh" "$stage/usr/libexec/monitor-app/seed-retained-package.sh"
    if [ -n "$update_key" ]; then
        install -m 0644 "$update_key" "$stage/etc/monitor-app/update-key.json"
    else
        # No trust anchor supplied: the update agent reports itself disabled instead of trusting anything.
        printf '{}\n' > "$stage/etc/monitor-app/update-key.json"
        chmod 0644 "$stage/etc/monitor-app/update-key.json"
    fi
    find "$stage/opt/monitor-app" -name "*.a" -delete
    chmod 0755 "$stage/opt/monitor-app/MonitorApp.Peer.Node"
fi
if [ -n "${MONITOR_APP_SAVE_STAGE:-}" ]; then
    tar -C "$stage" -czf "$MONITOR_APP_SAVE_STAGE" .
fi
want=",${MONITOR_APP_PACKAGES:-deb,rpm},"

# ---- .deb ----
if [[ "$want" == *,deb,* ]]; then
deb="$work/deb"
cp -a "$stage" "$deb"
mkdir -p "$deb/DEBIAN" "$deb/usr/share/doc/monitor-app"
install -m 0755 "$here/debian/postinst" "$here/debian/prerm" "$here/debian/postrm" "$deb/DEBIAN/"
echo "/etc/monitor-app/node.conf" > "$deb/DEBIAN/conffiles"
printf 'Monitor App peer node packaging: see docs/ARCHITECTURE.md and docs/TESTING.md in the source repository.\n' \
    > "$deb/usr/share/doc/monitor-app/README.packaging"
size_kb="$(du -sk "$deb" | cut -f1)"
cat > "$deb/DEBIAN/control" <<EOF
Package: monitor-app
Version: $version
Section: admin
Priority: optional
Architecture: amd64
Installed-Size: $size_kb
Depends: libc6, libicu | libicu74 | libicu72 | libicu70, libssl3 | libssl3t64, ca-certificates
Maintainer: Monitor App maintainers <noreply@invalid>
Description: Monitor App peer node (always-on service)
 Runs as the unprivileged monitor-app system user under systemd with state
 in /var/lib/monitor-app. The admin page is loopback-only and the peer listener
 binds to loopback unless /etc/monitor-app/node.conf says otherwise.
 Not enabled or started on install. Removing the package keeps the node
 identity and data; purging deletes them.
EOF
find "$deb" -type d -exec chmod 0755 {} +
dpkg-deb --root-owner-group --build "$deb" "$out/monitor-app_${version}_amd64.deb" >&2
echo "$out/monitor-app_${version}_amd64.deb"
fi

# ---- .rpm (spec is always usable; build only when rpmbuild exists) ----
if [[ "$want" != *,rpm,* ]]; then
    :
elif command -v rpmbuild >/dev/null 2>&1; then
    rpmtop="$work/rpm"
    mkdir -p "$rpmtop"/{BUILD,RPMS,SOURCES,SPECS,SRPMS}
    cp -a "$stage" "$rpmtop/SOURCES/stage"
    rpmbuild -bb --define "_topdir $rpmtop" --define "pkg_version $rpm_version" "$here/monitor-app.spec" >&2
    cp "$rpmtop"/RPMS/*/*.rpm "$out/"
else
    echo "rpmbuild not found: skipped RPM. On Fedora/RHEL run this script (it stages sources and calls rpmbuild -bb on $here/monitor-app.spec)." >&2
fi
