# How Claude and ChatGPT work on Monitor App 2.x

Monitor App 2.x lives in `lite/`. It replaced the old .NET app; nothing
outside `lite/` is built for release any more. Two agents work on it for the
owner, Wilson:

- **ChatGPT**, branches `chatgpt/*`: owns **all UI**.
- **Claude** (Claude Code), branches `claude/*`: owns **everything behind
  the UI**.

`AGENTS.md` still applies. This file adds who owns what and the contract
between the two halves. The ordered plan for both is `docs/ROADMAP.md`.

## 1. Who owns what (from 2026-11-27)

| Area | Owner | Paths |
|---|---|---|
| Screens, map, Dive, device page, Setup, widgets, theme, app state that only serves the UI | ChatGPT | `lite/app/lib/**`, app tests and assets |
| The data contract | Claude | `lite/FORMAT.md` |
| Core library: reading the folder, reports, status, hierarchy, history, patterns, controls | Claude | `lite/core/lib/**`, `lite/core/test/**` |
| Collectors: Linux / Windows / Docker agent, Android collector | Claude | `lite/core/lib/src/agent/**`, `lite/core/bin/**`, Android `Collector.kt`, `History.kt`, `Alerts.kt`, `AlarmReceiver.kt`, `Installer.kt`, `Migration.kt` |
| Packaging, installers, Docker files, update scripts, CI, releases | Claude | `lite/packaging/**`, `.github/workflows/lite.yml` |

Rules at the boundary:

- **UI reads only through `package:monitor_core`.** No file paths, JSON
  parsing or folder walking in UI code. If the UI needs something core does
  not offer, ask (section 3); do not work around it.
- **Claude does not change UI.** If a backend change breaks a UI call (for
  example a renamed core function), Claude makes the smallest compile fix
  only and says so in the PR.
- **ChatGPT does not change core, collectors or file formats.** A UI idea
  that needs a new field gets it by asking.
- `MainActivity.kt` (the channel between Android and Dart) is shared: either
  may change it, with a note in the PR.

## 2. The contract

**`lite/FORMAT.md` is the data interface.** It covers only what is written
today:

- the folder layout (`devices/<id>/now.json`, `history/`, `patterns.json`,
  `things/`, `requests/`, `results/`, `log.txt`, `watch/`, `settings.json`);
- every `now.json` key, the `readings` list and `src` marking (measured,
  declared, estimated, unknown);
- hierarchy: `node` (`type`, `parent`, `children`, `links` with `via` and
  `dir`), attached things, `params.json`;
- dual-boot twins (alpha / desktop, laptop / laptop-f);
- the things written today: UPS, audio interface, Docker Compose projects
  and containers, with their reading keys;
- controls: what a device offers to change, and the request / result files;
- history and patterns;
- **the `monitor_core` calls the UI uses**, and what stays stable.

Guarantees (FORMAT.md "What stays stable"): keys and paths are only added,
never renamed or removed without bumping `v`; core's public API keeps its
names. Readers keep unknown fields and treat a missing key as "not
available".

The UI must show values honestly: `src: estimated` and `src: unknown`
readings are marked as such, `level` (`warn` / `fault`) is the device's own
judgement, and `Level.inactive` (a twin that is not booted, a device off for
days) is gray, not red.

## 3. Asking for a backend change

Open a GitHub issue titled `data: <what the UI needs>` (or say it in the
`chatgpt/*` PR description), with the screen it is for and an example value.
Claude adds it to the collectors and core, documents it in FORMAT.md in the
same PR, and answers with the core call to use. Until it is merged, the UI
shows "not available", never a made-up value.

## 4. Working rules

- Small PRs against `main`. CI (`Monitor Lite`: `dart analyze` and
  `dart test` in core, `flutter analyze` in the app, Android, Windows and
  Linux builds) must be green before merge.
- Phone and tablet are the primary test targets. Nothing redraws with the
  screen off; the app adds no polling beyond core's watch / refresh cycle.
- Real screenshots only, no mockups. Docs describe only what works.
- Security first: no new open ports; no biometrics or PIN (Wilson has none,
  so use in-app confirm dialogs); signing keys never leave Wilson's machine.
- Don't ask Wilson diagnostic questions: each device's log is in the folder
  (`readLog(id)`).

## 5. Releases

Claude runs releases: Actions > **Monitor Lite** > Run, on `main`, with
`version` (for example `2.6.5`) and `notes` ("what you'll see"). It tags
`lite-v<version>` and publishes the Android APK, Windows installer, Fedora
.rpm, Ubuntu .deb and the Docker collector files. UI changes ship the same
way; ChatGPT can ask for a release in its PR.

## 6. Suggested UI direction from the backend

Suggestions from Claude, kept current as data is added. UI decisions are
ChatGPT's; nothing here is an order.

### Data that is ready and not yet shown (or barely)

| Data | Where | Idea |
|---|---|---|
| Pattern history | `readPatternHistory(dir, now)`, `seriesOf(lines, key)` | A small "over the months" line under each pattern (battery drain trend shows battery ageing) |
| Patterns from phones | each phone's `patterns.json`, or `findPatterns` | Same patterns section on phone pages as on computers |
| UPS source | reading `ups.source` ("USB", "UPower on the host (exported file)", ...) | Say where the UPS numbers come from; when it is UPower, `ups.load` is unknown and there is no alarm control, so hide the beeper row instead of showing it disabled |
| Settings from any device | `controls`, `sendRequest`, `myRequests`, `resultOf` | Already on device pages; a single "Waiting for…" list across devices would help on a phone |
| Twins | `node.links` `via: dual-boot`, `Level.inactive` | One machine card, the running system on top, the other as a quiet line ("Fedora, last up 2 h ago") |
| UPS patterns (2.6.7) | the UPS thing's `patterns.json` (`readPatternHistory(thingDir, now)` for months) | "On battery 2 times, 17 min in all", usual load, runtime when full (estimated); the runtime line over months shows the battery ageing |
| One-way reachability | `assess()` title "Can't be reached" | A device that reaches others but blocks incoming connections (the Pixel today) is a warning with its own wording, not "offline" |
| Shared model (2.6.7, row 5) | `ModelStore(root, myId)`: `list()`, `get`, `put`, `retire`, `history`, `asOf`; FORMAT.md "Model" | Real storage for #81's planning workbench and an "Add a thing" / "This is mine" action. Items survive restarts and appear on every device. Show `src` on every prop. Conflicts resolve on their own; `health().conflicts` lists them until then |
| Projects and racks (2.6.7, row 6, answers #81) | `ProjectStore(root, myId)`, `Project`, `Assembly`, `Placement` (`item`, `thing`, `u: (bottom, top)`, `face`), `Qty`; `assembly.check()` gives problems as sentences, weight/power `Total`s with coverage, free units; FORMAT.md "Projects" | Swap #81's in-memory draft for `put`/`get`; the checks it does in the UI can come from `check()` so every device agrees |
| Events (2.6.7, row 7) | `readEvents(root, from, to)`; FORMAT.md "Events" | A quiet list per device ("Power cut 21:04, back 21:31"); markers on history charts |
| History gaps (2.6.7, row 4) | `findGaps(times, restSecondsFor(kind), from:, to:)`, `explainGaps(...)`, `Gap.why` (off, asleep, collector, unknown) | A quiet band on history charts where a device wrote nothing; only `collector` is a problem worth colour |
| Automatic updates (2.6.7) | `settings.json` `update.auto` (default true), each device's log `update:` lines | One switch in Setup ("Install updates on their own"); "Update all" can show each device's version catching up |
| Service health (2.6.8, row 10, answers #89) | container `ct.http` (status or `no answer`), `ct.httpMs`; Pi-hole `ph.*` on the Pi-hole container; FORMAT.md container row | #89's health dot beside Docker health: `ct.http` level is the colour. `ph.api` = `needs its password` is a setup note, not a fault |
| Unplaced rack height (2.6.8, answers #81) | `Placement.height` (int U), `Placement.units` | Save the 2U of an amplifier with no position yet; `check()` says when a placed range disagrees |
| Android parity (2.6.8, row 9) | phone `st.state`, `ts.state`, `drive.*` | Syncthing peer/folder counts on phones need a place to paste Syncthing-Fork's API key (Settings > GUI in that app). Proposal: a field on the phone's own page that saves to the app's private settings, never to the shared folder |
| Network layers (2.6.8, row 11) | `net.gateway`, `net.dns`, `net.dnsDirect`, `net.internet`, `net.ifaces`, `net.pihole`; `assess().network` titles | Home's network line already shows `assess().network.title`, which now says which layer failed. A device's Network part can show the four checks as a row of four dots (router, DNS, public DNS, internet) |
| Docker | things of type `compose-project` / `container` | Project rows inside the host box; `ct.url` as the tap target; `cp.problems` is the only thing worth colour |

### Wilson's recorded preferences (from his feedback)

* Bare-bones means **uncluttered but polished**, not primitive: real
  typography, spacing and motion, little content. Quiet grey for what works;
  colour only for problems. Details on tap, not always on screen. Never lines
  through text.
* Map: **structured, deterministic tiers** (private network on top, then
  servers, computers, phones), clean rectangular regions, no force-directed
  blob. It must work for any number of devices.
* **Hardware children** (UPS, audio interface) are their own nodes cabled to
  the host (USB / power). Only **software** (Docker projects and containers)
  shares the host's box, and that box is tight.
* Dive: tap a device to zoom into it until it is its own page, then into its
  parts; breadcrumb back.
* Always mark **measured vs declared vs estimated vs unknown** (`src`);
  never show a guess like a measurement.
* Phone and tablet first. Real screenshots only, no mockups. He judges by
  what he sees on his own devices.
* Dual-boot: the system not running is normal, never a red flag.

### Known gaps (backend, being worked on)

* Docker collectors report no ICMP ping time. From 2.6.6 every computer also
  writes `peers.<id>.tcpMs` (`PeerView.tcpMs`), the TCP handshake time; show
  it when `ping` is missing (it is a different measure, so label it).
* wc-desktop is pinned to 2.6.3 and reads no UPS data by Wilson's choice:
  its UPS thing shows unknown values with a reason. Show the reason, not an
  error.
* Windows and Android write no things (USB devices) yet.
