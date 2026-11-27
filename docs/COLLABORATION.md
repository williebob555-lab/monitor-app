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

### TOP PRIORITY: Wilson's own complaints (2026-11-27 evening)

1. **UPS status takes too many submenus.** Wilson: "I hate how many
   submenus I have to go through just to read off the UPS status." Put it on
   Home (or the first screen), readable with zero taps, one tap at most for
   detail. Data: `glance(folder.readThings())` -> `GlanceLine` (`label`
   "UPS", `text` "On mains, 100 %, 70.5 min" / "On battery, 64 %, 22 min
   left", `level`, `thingId` to open). It also lists disks with warnings.
   Order: power source, charge, runtime.
2. **Dual-boot machines look like two machines.** wc-alpha/wc-desktop and
   laptop/laptop-f must be ONE row/box everywhere (lists, Dive, graphs), not
   only on the map: `assess().machines`, `systemsOf(id)` (table below).

### Data that is ready and not yet shown (or barely)

| Data | Where | Idea |
|---|---|---|
| Pattern history | `readPatternHistory(dir, now)`, `seriesOf(lines, key)` | A small "over the months" line under each pattern (battery drain trend shows battery ageing) |
| Patterns from phones | each phone's `patterns.json`, or `findPatterns` | Same patterns section on phone pages as on computers |
| UPS source (2.6.8) | reading `ups.source` in words ("UPower 1.91.5 on the host (exported file), plus USB HID for load and alarm"), and `from` on each UPS reading (`upower`, `upower-export`, `hid`, `nut`, `sysfs`) | Say where the UPS numbers come from, per row if you like (a small "UPower" / "USB" tag). With UPower only, `ups.load` is unknown and there is no `ups.alarm` or alarm control: hide the beeper row instead of showing it disabled |
| UPS host protection (2.6.8) | reading `ups.hostAction` (declared), e.g. "host powers off at 20 %" | A calm line on the UPS page: what the host does at low battery. It is the host's own UPower setting, read-only: no button |
| UPS identity (2.6.8) | reading `ups.identity` | Under the UPS's details: "USB 0764:0601 on port 1-5 (no serial)"; its note says moving it to another port gives it a new id |
| Unavailable vs zero | `value: null` with `src: "unknown"` and a `note` (FORMAT.md "Unavailable is never zero") | Never draw null as 0 or an empty bar: show "unknown" and the note. A 0 is a real zero |
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
| Who may change settings (2.6.8, row 14) | `controlsAllowed(folder)` (null = everyone), `settings.json` `controls.allow` | An "Allowed to change settings" list in Setup; save it into settings.json without touching other keys |
| History health (2.6.8, row 13) | `checkHistory(deviceDir, now, Retention.load(folder))` -> `.problems`, `.readMs` | Setup's storage line can say "all green" or the first problem; please time `readHistoryRange` for a year on the tablet and show it there |
| Unusual for this device (2.6.9, row 16) | readings group Unusual, keys `unusual.*`, value is the sentence | A quiet note on the part it concerns (key after `unusual.` is the reading key); never a red alarm |
| Topology over time (row 17) | `topologyAt(root, t)` -> `Moment` (`devices[id].up`, `.reach`, `.things`, `.containers`, `fromHourly`) | The timeline drag on the map: rebuild the map at the dragged time; say "hourly" when `fromHourly` |
| Rules files (row 18) | `RuleSet.load(folder)` -> `rules` and `problems` (file name -> sentences); `RuleSet.save(folder, rule)`; `validateRule(json)` | A rule editor: show `problems` next to the field; save only through `RuleSet.save`. Rules fire from 2.6.9 (row 19) |
| Default rules (row 21) | `rules/<id>.json` for the six defaults, all `enabled: false` | A rule list with on/off switches; turning one on is `RuleSet.save(folder, rule with enabled: true)` |
| Observed to modeled (row 22) | `suggestLinks(folder.readReports(), folder.readThings(), items, now)` -> `LinkSuggestion` (`name`, `type`, `observes`, `parentObserves`, `relinkItem`); `acceptLink(ModelStore(root, me), s)` | "This is mine" on a device or thing page; "This is your <item> on a new port" for a relink |
| Connector library (row 23) | `Library.load(root)`: `connectors` (id, label, family, fit, genders, pins, signals, alsoMates, note), `signals` (kind, direction, limits), `family(f)`, `mates(plug, receptacle)`, `common(a, b)`, `problems` | Port pickers grouped by family; show `note` (the traps: RP-SMA, TRS MIDI A/B, barrel sizes) |
| Connection check (row 24) | `checkChain(Port, [Link], Port, want:)` -> `ChainVerdict` (`verdict`, `layers[]`: `layer`, `verdict`, `evidence`, `reasons`) | "Why this works / won't" on a connection: one line per layer, reasons as written, evidence shown as verified/estimated/unknown |
| Physical constraints (row 25) | `checkPhysical(assembly)` -> `Finding` (`topic`, `level`, `evidence`, `text`); fields in FORMAT "Physical constraints" | Warnings drawn on the rack/board/cabinet; estimated findings look different from verified ones |
| What's missing (row 27) | `projectNeeds(project, items)` -> `lines` (`name`, `qty`, `status` owned/to-buy/missing, `why`), `toBuy`, `connectionProblems`, `unconnected`, `physical`, `openTasks`; project `connections` in FORMAT | The BOM list in a project: owned vs to buy vs missing, then the issues |
| One past moment (row 28) | `systemAt(root, t)` -> `items`, `projects`, `live` (Moment), `upAt(item)` | One timeline for the map, inventory and projects |
| Location, opt-in (row 29) | Android channel `location` -> `{on, granted}`, `setLocation({on})` (asks for coarse permission); reading `loc` = "lat,lon" (about 1 km), note has age and accuracy | A switch on the phone's own page, off by default; show `loc` as a place, never on other devices' pages unless switched on |
| Products (row 30) | `ProductStore(root, me)`: `put/get/ids/asOf`, `of(item)`, `link(ModelStore, item, productId)`; `Product` (`maker`, `model`, `specs`, `images`, `docs`, `offers`, `bestOffer()`) | Product lens of an item: photo, specs with src, manuals, offers; manual entry form |
| Notifications (row 20) | Nothing new for the UI: the phone's "Notify me on this phone" switch now becomes rule files (FORMAT "Phone switches"); the notify action's `on` lists devices | A rule editor's notify action: "show on" every device or chosen ones |
| Why a rule fired (row 19) | `firedLines(root, ruleId)`: `t`, `subject`, `since`, `why`, `did`, `notify`, `by`; `rule.fired` events | "Why did this fire" view on a rule; `rule.fired` events as notes on the history |
| One machine, two systems (2.7.1) | `assess().machines` -> `Machine` (`current`, `others`, `systems`, `name`, `level`); `assess().systemsOf(id)` | Wilson (2026-11-27): wc-alpha/wc-desktop and laptop/laptop-f must look like ONE machine everywhere, not just on the map. Device lists, home rows, Dive and graphs: one row per `Machine`, titled by the running system, the other system shown as "also boots Fedora/Windows", history graphs drawn across `systemsOf(id)` |
| Disks (2.7.1, row 38) | things of type `disk` (FORMAT "Things written today") | Disks under their host like the UPS: health dot, temperature, wear bar |
| Containers not visible (2.7.1) | reading `ct.access` = `not visible`, reason in `note` | Show the note on the host's Containers part instead of an empty list |
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
* wc-desktop reads no UPS data until its host UPower export is set up (it has
  no USB HID access by design): its UPS thing shows unknown values with a
  reason. Show the reason, not an error.
* Windows and Android write no things (USB devices) yet.
