#!/usr/bin/env bash
# Generates pre-release notes: commits since the previous preview tag plus "what's left" from TODO.md.
# Usage: eng/release/preview-notes.sh VERSION TAG [PREVIOUS_TAG] > notes.md   (run inside the repository)
set -euo pipefail
version="${1:?version}"
tag="${2:?tag}"
previous="${3:-}"
root="$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)"

echo "## Monitor App preview $version"
echo
echo "Test build for Windows, Fedora, Ubuntu and Android. Not a stable release; expect gaps."
echo
echo "### Which file do I need?"
echo
echo "| Device | Download | How |"
echo "| --- | --- | --- |"
echo "| Android phone or tablet | \`monitorapp-mobile-android-universal-$version.apk\` | Open it on the device and allow the install. |"
echo "| Windows PC | \`monitorapp-windows-x64-$version-setup.msi\` | Double-click it. |"
echo "| Fedora | \`monitorapp-node-fedora-x64-$version.rpm\` (+ \`monitorapp-desktop-fedora-x64-$version.tar.gz\` for the desktop app) | \`sudo dnf install ./<file>.rpm\` |"
echo "| Ubuntu | \`monitorapp-node-ubuntu-x64-$version.deb\` (+ \`monitorapp-desktop-ubuntu-x64-$version.tar.gz\` for the desktop app) | \`sudo apt install ./<file>.deb\` |"
echo
echo "Everything else is used by the apps themselves, so you don't need to download it: \`update-manifest.json\` and \`SHA256SUMS\` (the signed update list and checksums) and \`monitorapp-node-windows-x64-$version.zip\` (what installed Windows PCs download when they update). Devices that already have Monitor App offer this version as an update and install it only after you approve it."
echo
# "What you'll see" notes written by whoever prepared the release (docs/release-notes/next.md), when present.
if [ -s "$root/docs/release-notes/next.md" ]; then
    echo "### What you'll see"
    echo
    cat "$root/docs/release-notes/next.md"
    echo
fi
echo "### Changes since the previous preview"
echo
if [ -n "$previous" ] && git rev-parse --verify --quiet "refs/tags/$previous" >/dev/null; then
    range="$previous..HEAD"
    echo "Commits since \`$previous\`:"
else
    range="-30"
    echo "First preview or previous tag unavailable; last commits:"
fi
echo
# shellcheck disable=SC2086
git log --no-merges --format='- %s (%h)' $range | head -60
echo
echo "### What is in this build / known gaps"
echo
echo "Open items from TODO.md at this commit (unchecked tasks, shortened):"
echo
count=0
while IFS= read -r line; do
    text="${line#- \[ \] }"
    text="$(printf '%s' "$text" | sed -E 's/`//g')"
    if [ "${#text}" -gt 220 ]; then text="${text:0:217}..."; fi
    echo "- $text"
    count=$((count + 1))
done < <(grep -E '^- \[ \] ' "$root/TODO.md" || true)
[ "$count" -gt 0 ] || echo "- (none listed)"
echo
echo "### Install details"
echo
echo "- Ubuntu: \`sudo dpkg -i monitorapp-node-ubuntu-x64-$version.deb\`; Fedora: \`sudo rpm -U monitorapp-node-fedora-x64-$version.rpm\`; then review \`/etc/monitor-app/node.conf\` and \`sudo systemctl enable --now monitor-app\`."
echo "- Desktop app on Linux: extract \`monitorapp-desktop-<ubuntu|fedora>-x64-$version.tar.gz\` and run \`MonitorApp.Client.Desktop\`."
echo "- Windows: double-click \`monitorapp-windows-x64-$version-setup.msi\`. It installs the node service and desktop app; uninstalling keeps your data. (The zip with \`Install-MonitorApp.ps1\` still works from Windows PowerShell for manual installs.)"
echo "- Android: sideload \`monitorapp-mobile-android-universal-$version.apk\`."
echo "- Updates: installed nodes download and verify new previews automatically and wait for your approval (\`/api/v1/update\`). \`update-manifest.json\` is the signed manifest; \`SHA256SUMS\` lists every file."
