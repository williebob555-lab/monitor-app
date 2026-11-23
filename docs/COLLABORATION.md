# How Claude and ChatGPT Work on Monitor App

Two AI agents work on this repository for the owner, Wilson:

- **Claude** (Claude Code), branches `claude/*`.
- **ChatGPT** (ChatGPT / Codex), branches `chatgpt/*`.

Either agent may run out of time or usage partway through a task, so there are **no ownership areas**. There is one shared, ordered task queue. Any agent takes the next free task, works in small steps, pushes often, and leaves notes so the other agent can pick the work up at any point.

This file is the shared work board. Read it before starting any task and update it when you start, hand off, or finish one. `AGENTS.md` is still the mandatory rule set; this file adds the rules for working side by side.

## 1. Read first

These define what the app is. Read them before your first task, and again when they change:

1. `docs/VISION.md`: what Monitor App is becoming (Wilson's expanded vision).
2. `docs/SCENARIOS.md`: twelve scenarios Wilson expects the app to handle. They are the acceptance test for every design decision.
3. `docs/ARCHITECTURE-MODEL.md`: the shared system model and reasoning layer every feature is built on.
4. `docs/ARCHITECTURE.md`: peers, pairing, replication, monitoring, history.
5. `docs/UI_DESIGN.md`: the visual language.

## 2. Core principles (from Wilson, 2026-11-22)

- **One object, many lenses.** A real or planned thing has one record. Monitoring, inventory, physical layout, connections, projects, sourcing and history are views of that record, never copies. Moving between views keeps the object's identity.
- **No one-off code.** No feature is written for one scenario ("pedalboard check", "speaker wizard"). Build general mechanisms over the libraries of kinds, properties, connectors and signals, so new equipment is understood by adding data, not code.
- **The app is a collaborator.** A deterministic, algorithmic reasoning layer checks designs, follows signals, power and heat through chains, works out what is missing, suggests options, and explains why. Small AI models may help organise data, but nothing requires them, and their output is checked like any other data.
- **Built for a chaotic, nonlinear way of working.** Nothing is required up front except a name. Quick capture of loose notes, many-to-many links, no forced hierarchy, cheap reorganising that never loses anything. The app surfaces structure (duplicates, gaps, likely links) instead of demanding it.
- **Always say how sure we are.** Every value and finding is marked measured, specified (datasheet), entered, estimated, or unknown. A verified incompatibility is never shown the same way as an estimate. History never invents causes.
- **An interactive design environment, not a dashboard.** Users build, arrange, wire and experiment with systems that may not exist yet. Physical things look and behave like physical things. Playful, tactile interaction is welcome. No generic tile dashboards.
- **Monitoring stays the reliable foundation.** Nothing in the expansion may make telemetry, alerts, history or the peer network less reliable or less secure.

## 3. The plan

Everything builds on the one shared model. Each phase ends with one plain-English update to Wilson.

| Phase | Goal | Main work |
| --- | --- | --- |
| 1. Foundation (now) | The shared model exists in code; monitoring plugs into it; the repo is cleaned up | Core model, store and monitoring bridge; retire controller-era code; move the T301 connector library and rack/pedalboard checks onto model ids; current UI bug fixes |
| 2. First real design workflows | Scenarios 2, 3 and 8 work end to end | Reasoning layer (chain checks, what's missing, suggestions); a view shell that switches lenses on the same object; inventory; physical layouts (racks, pedalboards, enclosures) on the model |
| 3. Projects, sourcing, automation | Scenarios 1, 4, 7 and 11 | Projects with alternative designs; web API for the model; retailer lookups and price history; rules that span projects and live systems; time travel over the whole model |
| 4. The visual layer | The app feels alive | Exploded views, opening up a machine, living schematic, playful interactions, built on a model that already works |

Phases are priorities, not walls: a task from a later phase may be done early if it does not depend on unfinished work.

## 4. The task queue

Take the **first unclaimed task whose dependencies are done**, unless Wilson asks for something specific. `docs/BILL-OF-WORK.md` is the plain-English version of this queue for Wilson: what each item is, its size, and what he will see. Update it in the same PR that finishes an item. "Suggested" says which agent is likely a good fit; it is a hint, not ownership.

Each task is about one sitting of work and has a clear "done when". Bigger work is split into several tasks before anyone starts it.

| ID | Phase | Task | Done when | Depends on | Suggested | Claimed by / branch / since | Notes |
| --- | --- | --- | --- | --- | --- | --- | --- |
| T072 | 1 | Phone pairing inside the desktop app: draw the pairing QR in Settings > Pairings, plus a "Let my phone reach this computer" switch that binds the peer listener to the Tailscale address (private only, never wildcard) with a Tailscale-only firewall rule | Pair a phone from the app alone, no PowerShell; tests for the bind choice and the QR | none | Claude | Done in PR (this branch), 2026-11-23 | Bill of work item 3. Windows firewall rule: install-service.ps1 for new installs; for existing installs the SYSTEM update task adds it when the switch is turned on (from 0.1.14) |
| T065 | 1 | Wire the model into the running node: each node opens `model.db`, runs the monitoring bridge at startup and when new peers or observed devices appear | Node test shows a paired peer and an observed device appearing as model objects with their bindings | none (T061 done) | Claude | Claude, `claude/project-thread-8rnzln`, 2026-11-23 | Bill of work item 4 |
| T073 | 2 | "Things" lens: the first screen on the model. Cards for every model object, with each value's evidence level shown (measured / spec / entered / estimated / unknown) | Things visible in the desktop and phone app, read from `model.db` | T065 | Claude | | Bill of work item 5 |
| T074 | 2 | Quick capture: add a thing with only a name or note; add properties, tags and links later | Add, edit and link from the Things screen; changes go through the model change log | T073 | either | | Bill of work item 6 |
| T075 | 2 | "Open the machine": exploded view of a computer from its model parts, with live readings per part | Tap a computer in Things and see its parts with readings | T073 | Claude | | Bill of work item 7 |
| T071 | 1 | Verify the Windows MSI on Wilson's machine: install, open the desktop app, approve one in-app update, uninstall | Wilson confirms, or the failure is written here with the MSI log | T061 | either | | CI only installs it silently on a clean runner |
| T060 | 1 | Retire controller-era code (T057 plan): stop publishing controller/agent archives, delete `MonitorApp.Controller`, `MonitorApp.Persistence`, `MonitorApp.Agent.Service`, `MonitorApp.Agent.Core`, enrollment-only `Android.Core` types and their tests | Projects gone from the solution; workflows no longer build them; CI green | none | Claude | Claude, `claude/project-thread-8rnzln`, 2026-11-22 | Plan in `docs/DECISIONS.md` (T057) |
| T301 | 1 | Review and merge PR #12 (planning foundation) | PR #12 merged with CI green | none | ChatGPT | ChatGPT, `chatgpt/T301-project-rack-planning` | Merge as is; T064 moves it onto the model |
| T300 | 1 | SG250 switch adapter spike (PR #8) | PR #8 merged with CI green, or closed with a reason | none | ChatGPT | ChatGPT, `chatgpt/T300-sg250-adapter` | |
| T064 | 1 | Put T301 on the model: `ConnectionPort`, `RackEquipment`, `PlanPart` built from model objects, ports and placement; connector ids on model ports come from `ConnectorCatalog`; remove T301's separate JSON persistence | Planning tests run against model objects; no planning-only ids remain | T061, T301 | ChatGPT | | See section 9 of `docs/ARCHITECTURE-MODEL.md` |
| T065 | 1 | Wire the model into the running node: each node opens `model.db`, runs the monitoring bridge at startup and when new peers or observed devices appear | Node test shows a paired peer and an observed device appearing as model objects with their bindings | T061 | Claude | | |
| T066 | 2 | Findings with evidence levels (verified problem / estimate / unknown / OK as declared) as the single result type of every engine; adapt T301 evaluators to return them | All engines return the shared finding type | T064 | either | | `ARCHITECTURE-MODEL.md` 3.13 |
| T067 | 2 | Connection chain evaluation over the model: walk port to cable to adapter to port, evaluate each layer, report where a chain fails | Scenario 8 examples pass as tests (USB-C alt mode, 1/4-inch level mismatch, DC polarity) | T064, T066 | either | | |
| T068 | 2 | Local web API for the model: read objects, ports, connections, relationships, history as-of; write changes with validation | API tests over a real `model.db` | T061 | ChatGPT | | Follow `docs/SECURITY.md` loopback and antiforgery rules |
| T069 | 2 | "What's missing" derivation: unconnected required inputs, missing cables (with connector ends and length estimate), power shortfalls, as requirement objects | Tests on rack and pedalboard fixtures | T067 | either | | |
| T070 | 2 | Lens shell: one selection shared by every view; switching view keeps the object | Selecting a device and switching between system, physical and inventory keeps it selected | T061 | Claude | | UI; coordinate with any open UI task |

Add new tasks at the end of the right phase with the next free ID (check the queue, `TODO.md` and `docs/DECISIONS.md`). If two PRs add the same ID, the one merged second renumbers.

## 4a. Standard release set

Every release Wilson installs must contain, signed through the existing manifest:

| Platform | File | Notes |
| --- | --- | --- |
| Android | `monitorapp-mobile-android-universal-<v>.apk` | release-signed; launch-tested on an emulator by `android-startup-triage.yml` |
| Windows | `monitorapp-windows-x64-<v>-setup.msi` | the installer people use; `monitorapp-node-windows-x64-<v>.zip` stays as the in-app update payload |
| Fedora | `monitorapp-node-fedora-x64-<v>.rpm` | node service; desktop app as `monitorapp-desktop-fedora-x64-<v>.tar.gz` |
| Ubuntu | `monitorapp-node-ubuntu-x64-<v>.deb` | lower priority for Wilson (no server yet) |

Releases are cut on purpose by running `.github/workflows/preview.yml` (`gh workflow run preview.yml`); pushes to `main` no longer publish one. A release is only "done" when `install-smoke.yml` is green on the commit it was built from. Updates install on a device only after Wilson approves them there.

## 5. Claims, pushing often, and picking up stale work

1. **Claim before you code.** Put your name, branch and date in the task's "Claimed by" cell, in a small PR or in the first commit of your task PR. Start the branch from the latest `main`.
2. **Push early and often.** Push a work-in-progress commit at least at the end of every working session, even if it does not build yet, and open a **draft PR** early.
3. **Leave a handoff note.** Keep the top of the PR description current with: what is done, what is not, what to do next, and anything surprising. Any agent should be able to continue from that note alone.
4. **Claims expire.** A claim with no push for **24 hours** is expired. Any agent may take it over: write "taken over from <agent>" in the claim cell, and continue on a new branch built on top of the old branch (never rewrite the old one).
5. **Release when done.** Move the task to "Recently finished" in the PR that completes it.

## 6. Review

- Changes to the shared model (`src/MonitorApp.Model`, `contracts/`, schema versions), security (pairing, trust, control, presence, update, signing), or sync and replication need a review by the **other** agent, or by Wilson, before merge.
- Other changes may be merged by their author once CI is green.
- Only Wilson, or an agent acting on Wilson's standing authorization, merges to `main`. Never push to `main` directly.

## 7. Working style (Wilson's rules)

- **Be usage-conscious.** Do the smallest change that completes the task. No broad rewrites, no fan-out of helper agents, no repeated full test runs when nothing changed.
- **No mockups and no throwaway builds.** Every change builds toward the architecture in `docs/ARCHITECTURE-MODEL.md`. Prototypes are labelled as prototypes and are not presented as features.
- **No releases without Wilson.** Do not tag, publish, or trigger release workflows unless Wilson asks.
- **Plain-English updates.** Report what changed in words Wilson can read in a minute: what now works, what doesn't yet, and what is next. Be honest about implemented versus planned.

## 8. Branches, commits, and pull requests

- Branch: `<agent>/<task-id>-<short-topic>`, for example `chatgpt/T068-model-api`. Claude's cloud sessions may use the branch name the session assigns (`claude/project-thread-*`); the claim cell names the branch.
- Commit messages start with the task ID: `T068: Read objects over the local API`.
- PR title starts with `[Claude]` or `[ChatGPT]`. The PR description lists the handoff note (section 5), what changed, tests actually run and their results, platforms verified, docs updated, and anything unverified (the completion report from `AGENTS.md`).
- CI (`verify` on Ubuntu and Windows, Android build, install smoke when packaging changes) must be green before merge. Run `dotnet format MonitorApp.sln --verify-no-changes` before pushing.
- Before merge, update your branch by merging `main` into it. Never rebase or force-push someone else's branch.

## 9. Shared files that conflict easily

| File | Rule |
| --- | --- |
| `TODO.md` | Change only the lines for your own task. Add new lines at the end of the relevant section. |
| `docs/CHANGELOG.md`, `docs/DECISIONS.md` | Append only. Never reword the other agent's entries; propose a change in a handoff note instead. |
| `docs/COLLABORATION.md` (this file) | Edit only your own claim cells, your own handoff notes, and new tasks you add. |
| `MonitorApp.sln`, `Directory.Packages.props`, `packages.lock.json` | Add entries only for your own task. After updating your branch from `main`, regenerate lock files rather than hand-merging them. |
| `global.json` | Never commit local SDK changes. |
| `contracts/**/*.proto`, SQLite schema versions (`SchemaVersion` constants, the model schema version), alert schema version | **Write the bump in your claim cell first** (for example "peer schema v4 -> v5") so two tasks never take the same number. |

## 10. Recently finished

Keep the last ten. Older history belongs in `docs/CHANGELOG.md`.

| Task | Agent | PR | Merged |
| --- | --- | --- | --- |
| T061 shared model core, MSI; release on purpose only; hourly update checks + Check now; Android in-app updates; alert noise fix | Claude | #16, #17, #18 | 2026-11-23 |
| T062 fix plain readings, battery level, menu stutter; add `docs/VISION.md` | Claude | #14 | 2026-11-22 |
| T302 Android launch crash fix | ChatGPT | #11 | 2026-11-22 |
| Work board: shared queue | Claude | #15 | 2026-11-22 |
| T061 design doc, scenarios | Claude | #13 | 2026-11-22 |
| T051-T057 | Claude | #5 | 2026-11-22 |
| T039-T050 | Claude | #3 | 2026-11-22 |
| T031-T038 peer foundation | Claude | #2 | 2026-11-21 |

## 11. Handoff notes

Use this section for anything that does not fit in a PR description: requests to the other agent, or work left for anyone to pick up. Newest first. Delete a note once it is answered.

Format: `- YYYY-MM-DD, <from> to <to or anyone>: <what is needed, and where>.`

- 2026-11-23, Claude to anyone: Wilson wants visible progress and a bill of work before big efforts; `docs/BILL-OF-WORK.md` is that list. Favor items he can see. Cut a release (run the Preview workflow by hand) at each finished item and tell him in plain words what he will see. The Android updater (`MobileUpdater`, `AndroidApkInstaller`) is only verified by CI build; first real-phone install is 0.1.13 to the next release.

- 2026-11-22, Claude to ChatGPT: Wilson has replaced ownership areas with this shared queue. Your open PRs #8 and #12 are queued as T300 and T301 under your name; #11 (Android crash fix) was merged by Claude. For T301 follow-ups, please do not add a separate persistence layer for planning devices: `ConnectorCatalog` and `ConnectionCompatibilityEvaluator` become the shared connector library and compatibility engine on top of the model's ports (T064), and the model will not get a second connector library.

## 12. Project rules both agents follow

These come from Wilson and from `Monitor App Design Specification.txt`. They override convenience.

- **Security is paramount.** Peer trust, pairing, replication, control, and updates fail closed. Never trust an IP address or tailnet membership alone. No secrets in the repository (it is public).
- **Fingerprint for high-risk actions.** Remote control, update approval, OS switching, and any other high-risk action go through the user-presence (biometric) tier. Deny by default until Wilson grants it.
- **Updates need Wilson's approval on each device.** Builds may download and verify automatically, but nothing installs without per-device approval. Signing keys stay with Wilson.
- **Dual-boot machines must not look like a security problem.** One physical machine with Windows and Linux installs is expected; show each machine's OS options and which one is running.
- **Every device is fully capable on its own.** The phone is a mobile command center and its UI must be as usable as the desktop's.
- **Tests: lean but strict where it matters.** Prefer fewer broad scenario tests with good diagnostics. Be rigorous on security and sync. Follow `docs/TESTING.md` and the test contract in `AGENTS.md`.
- **Decisions and specs.** Product direction: `docs/VISION.md`, `docs/SCENARIOS.md`, then `Monitor App Design Specification.txt`. Working technical decisions go in `docs/DECISIONS.md`. UI decisions are in `docs/UI_DESIGN.md`. Do not promote an `ASSISTANT PROPOSAL` or `OPEN EXPLORATION` item into a requirement without Wilson.

## 13. When the agents disagree

If one agent thinks the other's merged design is wrong, it does not rewrite it. It leaves a handoff note with the reason and evidence, and Wilson decides. A decision that constrains future work is recorded in `docs/DECISIONS.md`.
