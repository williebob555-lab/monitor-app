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
echo "Test build for Windows, Fedora, Ubuntu and Android. Not a stable release; expect gaps. See the install notes at the end."
echo
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
echo "### Install"
echo
echo "- Ubuntu: \`sudo dpkg -i monitorapp-node-ubuntu-x64-$version.deb\`; Fedora: \`sudo rpm -U monitorapp-node-fedora-x64-$version.rpm\`; then review \`/etc/monitor-app/node.conf\` and \`sudo systemctl enable --now monitor-app\`."
echo "- Desktop app on Linux: extract \`monitorapp-desktop-<ubuntu|fedora>-x64-$version.tar.gz\` and run \`MonitorApp.Client.Desktop\`."
echo "- Windows: extract \`monitorapp-node-windows-x64-$version.zip\`, then in an elevated PowerShell 7 run \`.\\Install-MonitorApp.ps1\` (node service and desktop app)."
echo "- Android: sideload \`monitorapp-mobile-android-universal-$version.apk\`."
echo "- Updates: installed nodes download and verify new previews automatically and wait for your approval (\`/api/v1/update\`). \`update-manifest.json\` is the signed manifest; \`SHA256SUMS\` lists every file."
