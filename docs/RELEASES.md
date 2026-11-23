# Releases and Update Trust

## Current status

The repository has an executable release and update-verification foundation. It does not yet contain a background self-installer. The current safe boundary is:

1. CI verifies source on Windows and Ubuntu.
2. A release job publishes self-contained controller, legacy agent, and Windows equal-peer preview archives.
3. A protected GitHub `release` environment supplies a P-256 signing key.
4. The release tool creates a signed, versioned manifest containing channel, release version, minimum compatible version, target platform and architecture, HTTPS URL, exact byte size, and SHA-256 for every artifact.
5. Production update code verifies the pinned public key, signature, channel, version direction, compatibility floor, platform, architecture, size, and hash before an artifact can be considered eligible.

Unsigned, incorrectly targeted, downgraded, truncated, or modified artifacts fail closed. No code in this repository silently installs an update.

`MonitorApp.Update` now also provides the apply layer; the node wires it for Linux packages (see Preview builds below): updates are staged automatically and applied only on an explicit approval, with health-gated rollback. Linux uses `dpkg -i`/`rpm -U` plus a retained last-known-good package; Windows uses side-by-side version directories and a `current` pointer; Android offers a verified APK for the system installer. Windows service execution, rpm, and node/installer wiring are unverified. See DECISIONS T032.

## GitHub configuration

Create a protected GitHub Environment named `release`. Require a reviewer for that environment before permitting release-signing access.

Configure these values:

- Environment secret `MONITOR_UPDATE_SIGNING_KEY_PKCS8_BASE64`: Base64-encoded unencrypted PKCS#8 P-256 private key.
- Environment variable `MONITOR_UPDATE_PUBLIC_KEY_SPKI_BASE64`: Base64-encoded SubjectPublicKeyInfo public key matching the private key.
- Environment variable `MONITOR_UPDATE_SIGNING_KEY_ID`: a non-secret stable identifier such as `release-2026-01`.

Generate a new key pair locally without printing private material:

```powershell
dotnet run --project eng/release/MonitorApp.ReleaseTool/MonitorApp.ReleaseTool.csproj -- `
  generate-key `
  --private-key-output C:\secure\monitor-update-private.txt `
  --public-key-output C:\secure\monitor-update-public.txt
```

Upload the private value directly to the environment secret, upload the public value and key id as environment variables, and then remove the local private file using the organization’s approved secret-handling process. Never commit either private-key material or a generated secret file. Back up the private key only in an approved encrypted secret store.

The public key must also be deliberately provisioned to a future updater or installer as a trusted key. Merely downloading a public key beside a manifest is not trust establishment.

## Creating a release

The signed release workflow accepts only an existing immutable tag in `vMAJOR.MINOR.PATCH` form. It verifies source first, refuses to overwrite an existing GitHub release, publishes artifacts, signs the update manifest, writes a human-readable checksum file, and creates the release.

Normal path:

```powershell
git tag v0.1.0
git push origin v0.1.0
```

The workflow can also be dispatched manually against an existing tag. A branch or an untagged commit cannot be released by this workflow.

The initial artifact names are:

- `monitorapp-controller-linux-x64-VERSION.tar.gz`
- `monitorapp-agent-windows-x64-VERSION.zip`
- `monitorapp-peer-windows-x64-VERSION.zip`
- `monitorapp-agent-fedora-x64-VERSION.tar.gz`
- `monitorapp-agent-ubuntu-x64-VERSION.tar.gz`

These are self-contained alpha archives, not installed-service packages. RPM, DEB, Windows service installation, clean upgrade, rollback, and uninstall remain separate release gates.

## Preview builds (pre-releases) and automatic updates

`.github/workflows/preview.yml` runs only when a release is cut on purpose (`workflow_dispatch`: Actions > Preview build > Run workflow, or `gh workflow run preview.yml`). Its notes start with a "Which file do I need?" table. Before creating a draft release or starting expensive platform builds, the `prepare` job unlocks the protected `release` environment and derives its public verification key from `MONITOR_UPDATE_SIGNING_KEY_PKCS8_BASE64`. That **same** derived public key and the configured key ID are passed to Linux/Windows package builds and the manifest signer. Preview builds do not read `MONITOR_UPDATE_PUBLIC_KEY_SPKI_BASE64`, so the signature and embedded client trust anchor cannot disagree due to a stale public-key variable. The private key is never included in job outputs or published artifacts. This is initial key provisioning, **not automatic key rotation**: an already installed client trusting an earlier key will not accept a manifest signed by a different private key. Version `0.1.<run_number>` (`PREVIEW_SERIES` in the workflow) only grows, so it satisfies the updater's anti-rollback check and is also the Android `versionCode`. It publishes tag `preview-<version>` as a GitHub **pre-release** whose notes list commits since the previous preview and the open items of `TODO.md` ("known gaps"). Assets are moved between jobs through a draft release (no third-party actions); the draft is published only after the manifest is signed and is deleted if any job fails.

Artifacts (all listed in the signed `update-manifest.json`, channel `preview`; the signature is embedded in the manifest envelope, `SHA256SUMS` is for humans):

- `monitorapp-node-ubuntu-x64-V.deb` and `monitorapp-node-fedora-x64-V.rpm`: the node service (rpm built in a `fedora:42` container from the same staged tree).
- `monitorapp-desktop-ubuntu-x64-V.tar.gz` / `monitorapp-desktop-fedora-x64-V.tar.gz`: Avalonia desktop app as a self-contained tarball (identical payload; not part of the packages so the node package stays headless).
- `monitorapp-windows-x64-V-setup.msi`: the Windows installer. Double-click it (or `msiexec /i <file> /qn`). It installs the node service and desktop app by running the same `Install-MonitorApp.ps1` as the zip under Windows PowerShell 5.1, so the side-by-side layout and in-app updates are identical. Reinstalling or installing a newer MSI keeps identity and data; uninstalling removes the service and binaries and keeps `%ProgramData%\MonitorApp`.
- `monitorapp-node-windows-x64-V.zip`: node and desktop app plus `Install-MonitorApp.ps1` (service plus Start menu shortcut; re-run it from a newer zip to update; the in-app update path uses this zip).
- `monitorapp-mobile-android-universal-V.apk`: release-signed with your keystore; sideload it.

Installed Linux nodes carry the public key and repository in `/etc/monitor-app/update-key.json` (derived from the protected preview signing key at packaging time). `NodeUpdateAgent` polls the GitHub releases API every ~6 h (jittered, plus on start), picks the newest `preview-*` pre-release, verifies the signed manifest, downloads and verifies the `.deb`/`.rpm`, and then:

- `MONITOR_APP_UPDATE_POLICY=approve` (default, confirmed by the project owner): wait for approval via `POST /api/v1/update/approve` (loopback, antiforgery; status at `GET /api/v1/update`). Phone approval over a live session is a TODO.
- `auto`: install right after staging. Switching is one line in `/etc/monitor-app/node.conf` plus `systemctl restart monitor-app`.

The unprivileged service cannot run `dpkg`/`rpm`, so it writes a request and the root `monitor-app-update.service` (started by `monitor-app-update.path`, same binary with `--apply-update`) re-verifies the signed manifest with the root-owned key, re-checks the digest on a private copy, installs, restarts, waits for the health marker the node writes at startup, and rolls back to the retained package on failure. The package scriptlets best-effort seed the retained last-known-good package on first install.

### Install smoke before release

`.github/workflows/install-smoke.yml` builds the same packages with `eng/packaging/*` (versions 0.9.1 and 0.9.2, throwaway update key, no secrets, no `release` environment) and installs them on Ubuntu (systemd), a Fedora container and Windows. It runs on pull requests and can be dispatched manually; run it green before relying on a preview for a platform claim.

### First preview checklist (one time, about 10 minutes)

1. In the repository, Settings, Environments: create an environment named `release` (a required reviewer is optional; each run then needs approval for the Android and signing jobs).
2. On your Windows machine run `pwsh eng/release/New-ReleaseSigningKeys.ps1 -Repository <owner>/<repo>`. It writes keys outside the repo, uploads nothing, and prints the commands. Install a JDK first (`winget install Microsoft.OpenJDK.21`) if you want the Android keystore created.
3. Run the printed `gh secret set ... --env release` commands. Secrets (environment `release`): `MONITOR_UPDATE_SIGNING_KEY_PKCS8_BASE64`, `ANDROID_KEYSTORE_BASE64`, `ANDROID_KEYSTORE_PASSWORD`, `ANDROID_KEY_ALIAS`, `ANDROID_KEY_PASSWORD`.
4. Set the repository variable `MONITOR_UPDATE_SIGNING_KEY_ID` using the generated key ID. The preview build derives the public verification key from the protected private key and does **not** require a separately configured public-key variable. For the **tagged signed-alpha** workflow (`release.yml`) also set `MONITOR_UPDATE_PUBLIC_KEY_SPKI_BASE64` to the matching public value as its deliberate trust anchor. That public variable does not control preview builds.
5. Actions, "Preview build", Run workflow (or push to `main`). If either preview signing secret or the key ID is missing or invalid, the protected `prepare` job fails **before** any draft release or platform builds. Back up the key folder, then delete the plain private-key file.

### Signing-key mismatch recovery (preview)

A previous preview failure may report `Configured update public key does not match the signing key` during `Sign the update manifest`. This indicates a mismatch between the environment's private key and the repository's public-key variable; it is **not** an Android, Linux, Windows, or SDK build error. The preview workflow now derives the package trust anchor directly from the protected signing secret and uses it for the signed manifest as well. Never disable the signature check, publish an unsigned manifest, or copy a private key into repository code.

If **existing devices already trust a different public key**, do not silently rotate the signer: first distribute a deliberately authenticated trust-set update or use the recovery process under `Key rotation and recovery`. The older `release.yml` signed-alpha path still requires a separately configured matching public-key variable and fails closed on mismatch. Keep that variable synchronized before using a tagged signed-alpha release.

## Android gate

The workflow records whether the Android project exists but deliberately does not claim or synthesize an APK. A downloadable Android release requires a separate job that:

- builds the real Android project with the pinned Android workload and SDK;
- signs the APK or application bundle with protected Android signing material;
- runs emulator instrumentation tests;
- adds the signed artifact to the manifest with the correct architecture;
- completes a physical-device smoke test before Android support is claimed.

## Key rotation and recovery

Key rotation is an explicit release event. A future updater must ship a trust set that contains the new public key before releases begin using it. Removing the old key is a later release after the supported fleet has crossed the trust-set update. Do not reuse a key id for different key material.

If the signing key is suspected compromised:

1. Stop the protected release environment.
2. Remove the secret from GitHub.
3. Preserve audit evidence and identify releases signed by that key id.
4. Generate a new key in an approved secure environment.
5. Distribute the new public trust anchor through a separately authenticated recovery path.
6. Do not weaken signature checks or permit an unsigned emergency update.

## Verification commands

```powershell
dotnet test tests/MonitorApp.Update.Tests/MonitorApp.Update.Tests.csproj -c Release
dotnet build eng/release/MonitorApp.ReleaseTool/MonitorApp.ReleaseTool.csproj -c Release
pwsh ./eng/verify.ps1
```

The GitHub release workflow itself remains unverified until it runs in the private repository with the protected environment and real release values configured.
