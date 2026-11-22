#!/usr/bin/env bash
# Builds a self-contained linux-x64 publish and packages it as a .deb (dpkg-deb) and, when
# rpmbuild is installed, an .rpm. Usage: eng/packaging/linux/build-packages.sh [version] [outdir]
set -euo pipefail

here="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"
root="$(cd "$here/../../.." && pwd)"
version="${1:-0.0.0-preview}"
out="${2:-$root/artifacts/packages}"
rpm_version="${version//-/~}"
work="$(mktemp -d)"
trap 'rm -rf "$work"' EXIT
mkdir -p "$out"

stage="$work/stage"
mkdir -p "$stage/opt/monitor-app" "$stage/etc/monitor-app" "$stage/usr/lib/systemd/system" "$stage/usr/lib/sysusers.d"
# Publish from a throwaway copy: a RID-specific restore rewrites every packages.lock.json it touches.
src="$work/src"
mkdir -p "$src"
(cd "$root" && tar --exclude='bin' --exclude='obj' --exclude='artifacts' -cf - src contracts Directory.Build.props Directory.Packages.props global.json MonitorApp.sln) | tar -xf - -C "$src"
dotnet publish "$src/src/MonitorApp.Peer.Node/MonitorApp.Peer.Node.csproj" \
    -c Release -r linux-x64 --self-contained -p:RestoreLockedMode=false -p:DebugType=None -p:DebugSymbols=false -o "$stage/opt/monitor-app" >&2
install -m 0644 "$here/monitor-app.service" "$stage/usr/lib/systemd/system/monitor-app.service"
install -m 0644 "$here/monitor-app.sysusers" "$stage/usr/lib/sysusers.d/monitor-app.conf"
install -m 0640 "$here/node.conf" "$stage/etc/monitor-app/node.conf"
find "$stage/opt/monitor-app" -name "*.a" -delete
chmod 0755 "$stage/opt/monitor-app/MonitorApp.Peer.Node"

# ---- .deb ----
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

# ---- .rpm (spec is always usable; build only when rpmbuild exists) ----
if command -v rpmbuild >/dev/null 2>&1; then
    rpmtop="$work/rpm"
    mkdir -p "$rpmtop"/{BUILD,RPMS,SOURCES,SPECS,SRPMS}
    cp -a "$stage" "$rpmtop/SOURCES/stage"
    rpmbuild -bb --define "_topdir $rpmtop" --define "pkg_version $rpm_version" "$here/monitor-app.spec" >&2
    cp "$rpmtop"/RPMS/*/*.rpm "$out/"
else
    echo "rpmbuild not found: skipped RPM. On Fedora/RHEL run this script (it stages sources and calls rpmbuild -bb on $here/monitor-app.spec)." >&2
fi
