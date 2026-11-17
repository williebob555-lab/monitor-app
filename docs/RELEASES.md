# Releases and Update Trust

## Current status

The repository has an executable release and update-verification foundation. It does not yet contain a background self-installer. The current safe boundary is:

1. CI verifies source on Windows and Ubuntu.
2. A release job publishes self-contained controller and agent archives.
3. A protected GitHub `release` environment supplies a P-256 signing key.
4. The release tool creates a signed, versioned manifest containing channel, release version, minimum compatible version, target platform and architecture, HTTPS URL, exact byte size, and SHA-256 for every artifact.
5. Production update code verifies the pinned public key, signature, channel, version direction, compatibility floor, platform, architecture, size, and hash before an artifact can be considered eligible.

Unsigned, incorrectly targeted, downgraded, truncated, or modified artifacts fail closed. No code in this repository silently installs an update.

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
- `monitorapp-agent-fedora-x64-VERSION.tar.gz`
- `monitorapp-agent-ubuntu-x64-VERSION.tar.gz`

These are self-contained alpha archives, not installed-service packages. RPM, DEB, Windows service installation, clean upgrade, rollback, and uninstall remain separate release gates.

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
