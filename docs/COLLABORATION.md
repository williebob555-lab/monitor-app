# Multi-Agent Collaboration

Two AI agents work on this repository for the owner, Wilson:

- **Claude** (Claude Code), branches `claude/*`.
- **ChatGPT** (ChatGPT / Codex), branches `chatgpt/*`.

This file is the shared work board. It says who is working on what, so the two agents never edit the same area at the same time. Read it before starting any task and update it when you start, hand off, or finish one. `AGENTS.md` is still the mandatory rule set; this file adds the rules for working side by side.

## 1. The five rules

1. **Claim before you code.** No branch, no edit, until your claim row is on `main` (section 6). If an area is already claimed by the other agent, do not touch it; pick something else or ask in a handoff note.
2. **Never push to `main`.** All work goes through a pull request from your own branch. Only Wilson, or Claude acting on Wilson's standing authorization, merges to `main`.
3. **Stay inside your claim.** If the work turns out to need files in someone else's claimed area, stop and write a handoff note (section 8) instead of editing them.
4. **Never rewrite the other agent's history.** Do not force-push, rebase, or commit to a branch with the other agent's prefix. Build on top of their merged work, or branch from their branch and open your own PR into it.
5. **Release the claim when done.** Move the row to "Recently finished" in the same PR that completes the work, or in a short board-only PR if you abandon it.

## 2. Ownership

Ownership means "ask before changing", not "the other agent can never touch it". It exists so that half-finished designs are not changed underneath their author.

| Area | Owner | Notes |
| --- | --- | --- |
| Peer trust, pairing, replication, sync, transport (`MonitorApp.Peer.Core`, `.Sync`, `.Replication`, `.Transport`, `.Service`, `contracts/`) | Claude | Security- and sync-critical. Changes from ChatGPT come as a PR that Claude reviews before merge. |
| Control channel, user presence / fingerprint, update pipeline (`MonitorApp.Peer.Control`, `MonitorApp.Client.Presence*`, `MonitorApp.Update`, `eng/`, `.github/workflows/`) | Claude | Same review rule. Signing keys stay with Wilson; no agent ever handles them. |
| Alerts, history (`MonitorApp.Peer.Alerts`, `.Persistence`) | Claude | Built in PR #5 (merged). |
| Web API: local HTTP API and admin page (`MonitorApp.Peer.LocalApi`, `LocalApiClient`) | ChatGPT | Assigned by Wilson 2026-11-22. Claude keeps `api/machines*` for T058. Changes to antiforgery, presence-gated or control endpoints get Claude review before merge. |
| Rack-mount organization: racks, rack units, mounted equipment, spaces (universal object model in `MonitorApp.Contracts` and its persistence) | ChatGPT | Assigned by Wilson 2026-11-22. Additive contract changes only; claim any `.proto` field or schema version first. |
| UI: Dive interface and device grid (`MonitorApp.Client.Ui`, `.Client.Core/Scene`, `docs/UI_DESIGN.md`) | Claude | In flight in PR #4. |
| Dual-boot host identity and OS switching (`Peer.Collection/Host/`, `contracts/monitor/v1/host.proto`) | Claude | T058/T059. |
| Everything else, including items in `TODO.md` with no claim row | Open | Claim it in section 6 first. |

Wilson can reassign any area by editing this table.

Open work that does not overlap Claude's current claims, good for ChatGPT to claim:

- The managed-switch adapter spike from `TODO.md` (new adapter project; read-only; credentials local).
- Coverage review of `docs/TESTING.md` obligations against existing tests, filing concrete gaps as `TODO.md` items (fix them in claimed PRs).
- Documentation accuracy passes outside the files listed in section 5 as claim-required.
- Fedora RPM and SELinux packaging, once T059 (which edits `eng/packaging`) has merged. Until then, leave a handoff note first.

## 3. Task IDs (avoid number collisions)

Task and decision IDs (`T0NN`) appear in `TODO.md`, `docs/DECISIONS.md`, `docs/CHANGELOG.md`, and commit messages. To avoid two agents taking the same number:

- **Claude** takes the next free ID from **T058 to T299**.
- **ChatGPT** takes the next free ID from **T300 to T499**.

Look at the claims table and `docs/DECISIONS.md` for the highest ID already used in your range.

## 4. Branches, commits, and pull requests

- Branch: `<agent>/<task-id>-<short-topic>`, for example `chatgpt/T300-fedora-rpm`. Claude's cloud sessions may use the branch name the session assigns (`claude/project-thread-*`); its claim row names the branch.
- Start every branch from the latest `main`.
- Commit messages start with the task ID: `T300: Build and verify the Fedora RPM`.
- PR title starts with `[Claude]` or `[ChatGPT]`. The PR description lists: the task ID, what changed, tests actually run and their results, platforms verified, docs updated, and anything unverified (the completion report from `AGENTS.md`).
- One task per PR where practical. Keep PRs small enough to review in one sitting.
- CI (`verify` on Ubuntu and Windows, Android build, install smoke when packaging changes) must be green before merge. Run `dotnet format MonitorApp.sln --verify-no-changes` before pushing.
- Before merge, update your branch by merging or rebasing **your own** branch onto the latest `main`. Never rebase someone else's branch.

## 5. Shared files that conflict easily

Several files are touched by almost every task. Edit them like this:

| File | Rule |
| --- | --- |
| `TODO.md` | Change only the lines for your own task. Add new lines at the end of the relevant section. |
| `docs/CHANGELOG.md`, `docs/DECISIONS.md` | Append only. Never reword the other agent's entries; propose a change in a handoff note instead. |
| `docs/COLLABORATION.md` (this file) | Edit only your own claim rows and handoff notes. |
| `MonitorApp.sln`, `Directory.Packages.props`, `packages.lock.json` | Add entries only for your own task. After updating your branch from `main`, regenerate lock files rather than hand-merging them. |
| `global.json` | Never commit local SDK changes. |
| `contracts/**/*.proto`, SQLite schema versions (`SchemaVersion` constants), alert schema version | **Claim required.** These are shared counters and wire formats. Write the version bump you need in your claim row (for example "Control schema v3 -> v4") so the other agent does not take the same number. |

## 6. Claims (in-flight work)

Add a row before starting. Remove it (move it to section 7) when the PR merges or you abandon the task.

| Task | Scope | Agent | Branch | PR | Areas / files touched | Shared bumps | Claimed |
| --- | --- | --- | --- | --- | --- | --- | --- |
| UI | Dive interface plus device-grid home; scene projection; workspace shell | Claude | `claude/project-thread-errw46` | #4 | `Client.Ui`, `Client.Core/Scene`, `Client.Desktop`, `docs/UI_DESIGN.md`, `docs/ui-concepts/` | none | 2026-11-22 |
| T058 | Dual-boot machines: one peer per OS install, grouped into a machine by a presence-confirmed link and a signed `host.descriptor` record; shows "booted into other OS" instead of offline | Claude | next Claude branch | | `contracts/monitor/v1/host.proto` (new), `Peer.Collection/Host/` (new), `Peer.Node`, `Peer.Persistence` (`machine_links` table), `Peer.LocalApi` (`api/machines*`), `Client.Core` (`MachineDto`, `IPeerDataSource`), `Client.Mobile/InProcessPeerDataSource` | Peer persistence schema bump | 2026-11-22 |
| T059 | Remote OS switching: GRUB one-time next boot through a root helper, `SwitchBootTarget` control command with a `power_control` grant and fingerprint presence, Wake-on-LAN | Claude | same branch as T058 | | `contracts/monitor/v1/peer.proto` (field 23), `Peer.Control`, `Peer.Node` (helper), `eng/packaging` (systemd path unit, maintainer scripts), `Peer.Collection/Host` | `peer.proto` field 23 | 2026-11-22 |
| T060 (reserved) | Retire controller-era code per the T057 plan: stop publishing the controller and agent archives, then delete `MonitorApp.Controller`, `MonitorApp.Persistence`, `MonitorApp.Agent.Service`, `MonitorApp.Agent.Core` | Claude | not started | | those projects, `MonitorApp.sln`, `.github/workflows/`, `eng/` | none | 2026-11-22 |
| T300 | Read-only managed-switch adapter spike for the existing Cisco SG250: configured switch identity, local credentials, reported vs inferred evidence, explicit unavailable/permission/error states; no edits to Claude-owned peer/proto/schema modules | ChatGPT | `chatgpt/T300-sg250-adapter` | | new `Peer.Collection/Switches` adapter, its isolated tests, scoped docs | none | 2026-11-22 |

| T301 | Hardware-independent project/scenario planning and multi-standard rack layout core (height/depth/19in/10in/23in/Eurorack/custom), testable panel projection and constraint findings; no Claude-owned UI changes | ChatGPT | `chatgpt/T301-project-rack-planning` | pending | new `src/MonitorApp.Planning` and `tests/MonitorApp.Planning.Tests`, project registration and scoped docs | no proto/schema bump | 2026-11-22 |

## 7. Recently finished

Keep the last ten. Older history belongs in `docs/CHANGELOG.md`.

| Task | Agent | PR | Merged |
| --- | --- | --- | --- |
| T051-T057 | Claude | #5 | 2026-11-22 |
| T039-T050 | Claude | #3 | 2026-11-22 |
| T031-T038 peer foundation | Claude | #2 | 2026-11-21 |

## 8. Handoff notes

Use this section to ask the other agent for something, or to leave work for it to pick up. Newest first. Delete a note once it is answered.

Format: `- YYYY-MM-DD, <from> to <to>: <what is needed, and where>.`

- (none yet)

## 9. Project rules both agents follow

These come from Wilson and from `Monitor App Design Specification.txt`. They override convenience.

- **Security is paramount.** Peer trust, pairing, replication, control, and updates fail closed. Never trust an IP address or tailnet membership alone. No secrets in the repository (it is public).
- **Fingerprint for high-risk actions.** Remote control, update approval, OS switching, and any other high-risk action go through the user-presence (biometric) tier. Deny by default until Wilson grants it.
- **Updates need Wilson's approval on each device.** Builds may download and verify automatically, but nothing installs without per-device approval. Signing keys stay with Wilson.
- **Dual-boot machines must not look like a security problem.** One physical machine with Windows and Linux installs is expected; show each machine's OS options and which one is running.
- **Every device is fully capable on its own.** The phone is a mobile command center and its UI must be as usable as the desktop's.
- **No throwaway scaffolding.** Each change should build toward the final architecture in `docs/ARCHITECTURE.md`. Do not add interim builds or mock layers that will be thrown away.
- **Tests: lean but strict where it matters.** Prefer fewer broad scenario tests with good diagnostics. Be rigorous on security and sync. Follow `docs/TESTING.md` and the test contract in `AGENTS.md`.
- **Decisions and specs.** Product authority is `Monitor App Design Specification.txt`. Working technical decisions go in `docs/DECISIONS.md`. UI decisions are in `docs/UI_DESIGN.md`. Do not promote an `ASSISTANT PROPOSAL` or `OPEN EXPLORATION` item into a requirement without Wilson.

## 10. When the agents disagree

If one agent thinks the other's merged design is wrong, it does not rewrite it. It leaves a handoff note with the reason and evidence, and Wilson decides. A decision that constrains future work is recorded in `docs/DECISIONS.md`.
