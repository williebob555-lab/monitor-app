# Monitor App roadmap: from 2.6 to full scope

The one ordered plan for Monitor App 2.x (`lite/`). Every agent working on
the backend follows it top to bottom and keeps it current. It replaces
`lite/ROADMAP.md`; the old app's `docs/BILL-OF-WORK.md` and `TODO.md` are
history.

Sizes are agent working time: **S** under an hour, **M** one to three hours,
**L** a night or more (split it into PRs of M or smaller).

## 1. End goal

From `docs/VISION.md` and `docs/SCENARIOS.md`: one self-hosted workspace to
design, build, organize, source, monitor, automate and evolve physical and
digital systems. Every object (a device, a part, a cable, a container, a
project, a product) has one identity seen through many lenses: live
telemetry, physical layout, connections and signals, inventory, project,
product, history. Every value says whether it is measured, declared (from a
spec or the user), estimated or unknown. Local first: no cloud, no
mandatory AI, works offline, cheap on phones.

The four stages from the vision, in priority order (not prohibitions):

1. **Monitoring foundation.** Reliable collection on every device with the
   app closed, history, diagnosis in words, alerts, basic control.
2. **Inventory and physical organization.** Owned assets, racks, connections
   between known parts.
3. **Design and planning.** Projects, assemblies, spatial and electrical
   constraints, compatibility, bills of materials.
4. **Research and intelligence.** Sourcing, price tracking, deeper analysis,
   rules across everything, optional AI.

The architecture rules that every item keeps:

* **The folder is the database.** Plain files in the Syncthing-shared folder;
  each device writes only its own subfolder; no server, no pairing.
* **Files are flexible and parsed well.** Unknown fields kept, keys only
  added, `src` on every value, hierarchy and in/out links declared in the
  files (`lite/FORMAT.md`).
* **History stays small and smart.** Bounded, downsampled, pruned by
  `settings.json`, condensed into learned patterns that outlive raw data.
* **Cheap.** Hard budgets, logged by every collector; nothing redraws with
  the screen off; faster only while someone is looking.
* **Honest.** Docs and screens describe only what works.

## 2. Who does what

* **Claude**: everything behind the UI: `lite/FORMAT.md`, `lite/core`,
  collectors (Linux, Windows, Docker, Android Kotlin collector), packaging,
  CI, releases. This roadmap is Claude's queue.
* **ChatGPT**: all UI (`lite/app/lib/**`). Each item below names the UI it
  enables and the core call to use; that column is a suggestion for
  ChatGPT's queue, not an order. See `docs/COLLABORATION.md`.
* **Wilson**: decisions marked **Gate** below, and anything that changes a
  deployed server config, a version pin or deletes something significant.

## 3. Standing order for long runs

When Wilson says "work for a while", "overnight" or "keep going", this is
the loop. It has no natural stopping point except the two listed in step 6.

1. **Start of each item**: re-read this file on `main`. Take the first row
   in section 6 whose status is `Todo` (or `Doing` left by an earlier
   session), whose dependencies are `Shipped` or `Verified`, and that is not
   `Blocked`. Set it to `Doing` in your branch.
2. **Before building**: if the item is about a fault, prove the cause from
   the device files and logs first (`C:\Users\willi\Infrastructure\Monitor`
   or `C:\Monitor App\Sync` on the laptop, `readLog(id)`). Do not ask Wilson
   diagnostic questions.
3. **Build it** in PRs of size M or smaller: code, tests, `lite/FORMAT.md`
   for any new field, this file's status row. Merge when CI (`Monitor Lite`)
   is green. Repo authority is standing: merges, pushes and releases need no
   approval.
4. **Ship it**: if a device would behave differently, run a release
   (Actions > Monitor Lite, next `2.x.y`, "what you'll see" notes). Status
   becomes `Shipped <version>`.
5. **Verify it later, never wait for it**: devices pick up releases on
   their own timers. At the start of each following item, spend at most 10
   minutes checking the oldest `Shipped` rows against the "Done when" check
   in real device files. Pass: `Verified`. Fail: diagnose, then fix it as
   the very next item. Nothing is called fixed until it is `Verified`.
6. **Then take the next item.** Finishing an item, merging a PR, publishing
   a release, reaching a phase boundary, or a quiet hour are **not** reasons
   to stop or to report and wait. Stop only when:
   * every remaining row is `Blocked` or a `Gate` with no answer, or
   * Wilson says stop.
   (Running out of usage stops a session anyway; leave the row `Doing` with
   a one-line note of where it stands, so the next session resumes it.)
7. **Blocked?** Never sit on it.
   * Needs Wilson (a gate, a setting on his device, a physical action):
     ask once with a decision card or one message saying exactly what to
     type and where, set the row to `Blocked: Wilson <what>`, and take the
     next item.
   * Technical (a tool missing, an API that does not behave): two serious
     attempts or one hour, whichever is first; write what you found in the
     row and in project memory, set `Blocked: <reason>`, take the next item.
8. **Reporting during a run**: keep the thread checklist current. Post to
   Wilson only for a gate question, a result he can see on his devices, or
   the end of the run. One end-of-run summary: what is `Verified`, what is
   `Shipped`, what is blocked and on what.
9. **Never, whatever the item says**: change deployed server configs or
   version pins (wc-desktop is pinned at 2.6.3 by Wilson's choice), touch
   `wc-services` or other containers, edit UI code (smallest compile fix
   only, said in the PR), delete anything significant, require biometrics
   or a PIN, add an open port, or use mockups in place of real screenshots.

Status words: `Todo`, `Doing`, `Shipped <version>`, `Verified <date>`,
`Blocked: <reason>`, `Gate: <question>`.

## 4. Where it stands (2026-11-27, read from the real device files)

Latest release: **lite-v2.6.6**. What works today (details in
`lite/FORMAT.md`):

* Every device writes `now.json`, hour/day history and its log into the
  shared folder with the app closed (Android by alarm, computers by the
  `monitor-agent`, servers in Docker). Live rate (10 s / 15 s) while any app
  is open.
* Diagnosis in words: offline, Syncthing stopped, network split, can't be
  reached (one-way), dual-boot twin not booted.
* Linux: system identity, CPU temperature, Tailscale, Syncthing, drives,
  Docker projects and containers, USB UPS by HID (or UPower export), audio
  interface. Android: battery health, Wi-Fi. Patterns on every device and
  the UPS, kept for years. Settings from any device (UPS beeper, update).
  Android notifications (opt-in per device). Nightly/15-min server updater.

The fleet right now:

| Device | System | Version | Seen | Notes |
|---|---|---|---|---|
| Wilsons-Laptop | Windows | 2.6.0 | now | behind; `OVER BUDGET: cycle 8872 ms > 8000 ms` |
| wc-laptop-f | Fedora (laptop's twin) | 2.6.0 | 10 h ago | not booted, normal |
| wc-pixel | Android | 2.6.6 | now | others time out reaching it (inbound block on the phone, see row 3) |
| wc-tab | Android | **2.4.0** | now | stuck two releases behind |
| wc-beta | Ubuntu, Docker | 2.6.6 | now | `OVER BUDGET: memory 66 MB > 40 MB` |
| wc-alpha | Ubuntu, Docker (desktop's twin) | 2.6.5 | 10 h ago | UPS verified live 2026-11-27 |
| wc-desktop | Fedora, Docker | 2.6.3 (pinned) | 11 h ago | no UPS by Wilson's choice |

Open UI work by ChatGPT: #77 map clarity, #81 planning workbench (waits on
row 5 for real storage), #82 pattern history, #84 request activity.

## 5. Gates waiting on Wilson

| Gate | Why it matters | Unblocks |
|---|---|---|
| Apply the host UPower export on wc-desktop (and wc-alpha): `/mnt/project-files/ups-upower/wc-desktop-upower-steps.md`. wc-desktop runs 2.6.6 since 2026-11-27 14:29Z, which reads the basics; 2.6.8 adds host protection, several UPSes and the HID supplement | UPS readings on the Fedora side | a deployment step, no row |
| Pixel: turn off "Block incoming connections" in the Tailscale app (or the Headscale ACL) | others can reach the phone | row 3 |

Answered 2026-11-27 20:47Z: shops Amazon, eBay, Walmart (rows 31-33); no container restarts (row 26 dropped); delete the old .NET code (row 36); AI on alpha/beta, small models on the device in use (row 37); SMART companion OK (row 38).

## 6. The queue

Worked top to bottom. "Done when" is checked in real device files unless it
says otherwise; tests alone are never enough.

### Phase A: the monitoring foundation is solid on every device

| # | Item | Size | Depends | Done when | UI it enables (ChatGPT) | Status |
|---|---|---|---|---|---|---|
| 1 | **Fleet on the current version.** Find from wc-tab's and the laptop's logs why 2.4.0 / 2.6.0 never updated (Android install hand-off, Windows `update` control), fix the path, release. | M | | every unpinned, booted device reports the newest `app` within a day of a release | "Update all" shows real progress | Doing: cause found (wc-tab and laptop had no automatic update path; only a tap or "Update now"). 2.6.7 adds automatic updates on Windows, Android and Linux packages |
| 2 | **Budgets honest.** Laptop cycle 8.9 s > 8 s and wc-beta 66 MB > 40 MB: find which step costs it (time each step in the log), fix, or move the budget with the reason in README. | M | | 24 h with no `OVER BUDGET` line in any device log | | Doing: laptop cause proven (Windows ping blocked the cycle 2 s per unreachable device; cpu 16 ms of 8.9 s), fixed in 2.6.7. wc-beta memory rises ~1 MB/min to ~67 MB since 2.6.5 (2.1.0 held 16 MB); 2.6.7 logs time and memory per step (`steps[...]` in the hourly line) to find which. 2026-11-28 logs: wc-beta (2.7.0) and the laptop (2.7.1) climb to ~61 MB in the first hour, spread over every step, then stay flat (every step +0.0 MB in later hours), so a resident heap, not a leak; wc-alpha (2.6.8) holds 29 MB. 2.7.3 adds `minRss` and the untimed `notify`/`tail` steps to tell heap from peaks before choosing fix or budget |
| 3 | **One-way phone.** Code side done (2.6.6 says "Can't be reached"). Remaining is the Pixel setting. | S | | wc-beta and laptop `peers.<pixel>.tcp` = `open` | | Done 2026-11-28: no setting needed (Android has none); the Pixel published its carrier address instead of Tailscale's, fixed in 2.7.1. wc-beta and the laptop both log `wc-pixel:open` since 2026-11-27 22:04Z |
| 4 | **Prove collection with the app closed, everywhere.** Core `gaps(deviceDir, range)` lists holes in history longer than 3x the device's rate; run it over all devices for the last 7 days; explain each hole from the log (off, asleep, Doze, crash). Also confirm the live rate: `next` 10/15 while watched, back to rest after. | M | 1 | every hole in the last 7 days is explained by power/boot, not by the collector | "Gaps" band on history charts; `findGaps()` / `folderGaps()` | Doing: `gaps.dart` + `tool/gaps.dart` built; log lines carry their UTC offset from 2.6.7. First run (2026-11-27, 2 days of history): every gap on alpha, beta, laptop, laptop-f is power/boot/dual-boot; wc-desktop 2026-11-26 15:16-19:32Z was a collector hang (others reached it 97 % of the time, 2.3.0 wrote one cycle and stopped). Needs the full 7-day window |
| 5 | **Model files, version 1 (unblocks #81).** `model/items/<id>.json`: one file per object (`id`, `type`, `name`, `props` with `src`, lifecycle `state` proposed/owned/installed/retired, `parent`, `ports`, `links`, `observes`: a device or thing id), written atomically with `rev`, `by`, `t`; each item keeps `history.jsonl` of its changes. Conflict copies resolved by newest `t`, reported in Folder health. Core `ModelStore` (list, get, put, delete-as-retire, `asOf`). FORMAT.md section. | L | | an item written on the laptop is read back unchanged on the Pixel and tablet; a forced Syncthing conflict resolves and shows in Folder health | everything in phases C-E; "Add a thing" | Doing: `ModelStore` + FORMAT.md "Model" built with tests (2.6.7); device round trip and a real conflict to verify |
| 6 | **Projects and racks data.** `model/projects/<id>.json` (goal, items, tasks, revisions) and assemblies with typed placements (rack U ranges, units on every dimension, front/rear), referencing model items and observed thing ids. Core validation: overlaps, bounds, depth, weight and power totals with coverage. Answers ChatGPT's #81 request. | M | 5 | #81's draft project survives an app restart and appears on another device | #81 planning workbench on real storage | Doing: `ProjectStore`, assemblies, placements and `check()` built with tests (2.6.7); verify once #81 saves through it Placement `height` (unplaced U size, #81 follow-up) added in 2.6.8. |
| 7 | **Events log.** Each device appends `devices/<id>/events/YYYY-MM.jsonl`: went offline/online (as seen by it), network split started/ended, power cut started/ended, container stopped/started/unhealthy, thing plugged/unplugged, update installed, control applied. Pruned with hourly history. Core `readEvents(range)`. | M | | a real power cut, container restart and app update each appear once, on the right device | event list, timeline markers | Doing: `events.dart` + agent + Android (`app.updated`, `control.applied`) built with tests (2.6.7); verify on the next real power cut, container restart and update |
| 8 | **Windows parity.** Tailscale (`tailscale status --json`), Syncthing (local REST, key from its config), drives, CPU temperature where WMI gives it, laptop battery health. Same keys as Linux. | M | 2 | the laptop's `now.json` has the Tailscale and Syncthing groups with true values | same parts as Linux devices | Doing: Syncthing group and drives already on the laptop (its history has `st.*`). Tailscale through tailscaled's named pipe (no child process, so no console flash) in 2.6.8; a failure shows as `ts-problem=` in the hourly log line. Battery health and CPU temperature need WMI/IOCTL calls that can crash the process if a struct is wrong and can't be tried here: left for a session with Remote Control on the laptop. 2026-11-28: the laptop's tailscaled answers its status API with 401 to the app, so the next release falls back to the Tailscale adapter for `ts.state` (estimated, says why) |
| 9 | **Android parity.** Syncthing-Fork state (its REST API on the phone), Tailscale up/down as far as Android exposes it (VPN active), storage per volume. | M | | Pixel and tablet show a Syncthing group whose values match the Syncthing app | | Shipped in 2.6.8, verify: `st.state` (local sync port), `ts.state` (VPN + 100.64/10 address), `drive.*` per volume when more than one. Peer/folder counts Blocked: Syncthing-Fork's API key is private to that app, so it needs a paste-in key field (UI, ChatGPT) |
| 10 | **Service health.** For each container with `ct.url`: HTTP status and response time (`ct.http`, `ct.httpMs`); Pi-hole: queries and blocked today, gravity age (its local API); Syncthing and Tailscale stay as they are. A container that answers 5xx or not at all gets `level: warn`. | M | | wc-beta / wc-alpha show Pi-hole numbers that match its own page, and a stopped Filebrowser shows a warning | service rows with a health dot (#89) | Shipped in 2.6.8, verify. Pi-hole numbers Blocked until the collector gets `MONITOR_PIHOLE_PASSWORD` (a deployed config change, Wilson's call); until then `ph.api` says so |
| 11 | **Network diagnosis, deeper.** Each computer and phone also checks its gateway, DNS (through Pi-hole and directly) and the internet (one small TCP connect), plus interface list with addresses. `assess()` adds: "internet down for everyone", "DNS failing (Pi-hole)", "this device's Wi-Fi only". | L | 7 | a deliberately stopped Pi-hole produces "DNS failing" in words on every device within two cycles | network line on Home says which layer failed | Shipped in 2.6.8, verify (stop Pi-hole on purpose only when Wilson says so). Windows has no router check (no safe route-table call yet) |
| 12 | **More things.** Windows USB devices as things (same id rule, so the dual-boot laptop keeps one identity); UPS over HID on Windows; audio xruns via PipeWire on Linux. | M | 8 | the laptop's USB devices appear under it with the same ids on Windows and Fedora | | Partly shipped in 2.6.8, verify: Windows lists its USB devices through SetupAPI (`usb.count`, `usb.list`, with vendor:product and serial so they can be matched to Fedora's ids), behind a crash guard that switches it off if it ever takes the collector down. Not done, nothing to test against yet: making them things with Fedora's ids (serial-less devices are named by port differently on each system), UPS over HID on Windows (the UPS is on the desktop), PipeWire xruns (the desktop collector runs in Docker without PipeWire) |
| 38 | **Host telemetry companion** (design: docs/COMPANION.md; file-exporter pattern approved by Wilson 2026-11-27, each provider follows the contract there). The UPower exporter grows into `host-export.sh` with opt-in sources; first SMART (one sandboxed root timer, `smartctl -j`, never wakes a sleeping disk), disks as things with model+serial ids. AMD GPU straight from sysfs in the collector; NVIDIA only if one appears. Docker stays on the GET-only proxy. | M | | wc-alpha and wc-desktop write a `disk` thing per drive with health, temperature and hours, the same ids on both systems | disk things on the map and Dive | Built (2.7.1): `smart-export.sh` (sandboxed root timer, every 30 min, `-n standby`), collector reads `/host/smart` into `disk` things (model+serial ids). Verify on wc-alpha / wc-desktop once Wilson runs the steps in packaging/docker/README.md "Disk health" |
| 13 | **History integrity on real data.** Every device folder under its `maxMb`; no `.sync-conflict` history files left; summaries present for every ended hour/day; `readHistoryRange` cost under 50 ms for a year view on the tablet. | S | 4 | a script over the real folder reports all green | | Shipped in 2.6.8, verify. Run 2026-11-27 over the real folder (`dart run tool/integrity.dart <folder>`): all devices far under 50 MB (largest 0.5 MB), no conflict copies, year view 0 ms; 4 hours on wc-desktop/wc-alpha had no summary because that machine boots with its clock 5 h off until it syncs (dual boot, hardware clock read as local time by one system). Fixed: the writer re-summarizes hours it gets lines for after a clock jump, history no longer pauses when the clock goes back, the log says `clock went back`. Each computer logs `history ok: ...` every 6 h. Tablet read cost in the app itself is for the UI to log |
| 14 | **Security pass for 2.x.** Who may ask for which control (`settings.json` `controls.allow`, default: every device in the folder); request size and key validation; stale-file and path-traversal checks in every reader; SECURITY.md rewritten for 2.x (what Syncthing and Tailscale protect, what the folder trusts). | M | | tests for each rule; SECURITY.md matches the code | an "Allowed to change settings" list | Shipped in 2.6.8. Found and fixed: a request id became a file name unchecked (`../` could write a result file outside the folder), a request could claim another device as `from`, and a now.json could claim another device id. All three now refused, with tests; `controls.allow` added on computers and Android; lite/SECURITY.md written |

### Phase B: smart history becomes knowledge

| # | Item | Size | Depends | Done when | UI it enables | Status |
|---|---|---|---|---|---|---|
| 15 | **Conditional patterns** ("usage X under condition Y"): CPU temperature by load, data per day by network type, battery drain by app-open vs closed, UPS runtime by load, container memory and restarts per day. Same `patterns.json` shape. | M | 7 | each appears on at least one real device with a sensible value | more pattern lines | Shipped in 2.6.8, verify: `battery.drain.watched` / `.quiet`, `net.perDay.wifi` / `.mobile` / `.wired` (phones and computers), `temp.cpu.byLoad`, `ups.runtime.byLoad`, container `ct.mem.usual` and `ct.restarts` (from events). History lines gain `w` and `nt`, so phone patterns need a day of 2.6.8 data |
| 16 | **"Unusual for this device".** Compare the current hour with the learned usual (patterns history); write `level: warn` with the reason ("CPU 70 %, usually 8 % at night"). Never on less than 3 days of history. | M | 15 | one real, explainable flag over a week and no flood (fewer than 3 a day per device) | a quiet "unusual" note on a part | Shipped for 2.6.9 on computers, verify over a week: `unusual.<key>` readings (group Unusual, warn, e.g. "CPU 70 %, usually 8 % at night") for CPU, memory, CPU temperature; only with 3+ earlier days at that time of day and far above the usual; at most 3 new a day; each new one logged as `unusual:`. Phones not yet (their CPU figure is only the collector's own) |
| 17 | **Topology over time.** Core `topologyAt(t)` from history `p` and events: who reached whom, what was plugged into what, which containers ran. Works over the full detail window, then hourly. | M | 7 | reconstructing last night's power cut on the real folder matches the logs | timeline drags the map and the hierarchy | Built (core only, nothing to install): `topologyAt(root, t)` and `dart run tool/timeline.dart <folder> <time>`. On the real folder, 03:30Z / 04:02Z / 04:10Z on 2026-11-27 show wc-alpha and wc-laptop-f going off around 04:00 and the Windows laptop coming up, as their logs say. Things beyond containers need their own history to show |

### Phase C: rules and automation (alerts become general)

| # | Item | Size | Depends | Done when | UI it enables | Status |
|---|---|---|---|---|---|---|
| 18 | **Rules files.** `rules/<id>.json`: trigger (any value path with condition and duration, an event type, or "unusual"), scope (devices/things/types), actions (notify, annotate history, control request), cooldown, enabled. FORMAT.md section. | M | 7 | format and validator merged with tests | rule editor | Shipped (core, format only; nothing runs a rule until row 19): FORMAT.md "Rules", `validateRule` gives problems as sentences, `RuleSet.load/save`. Tests in `rules_test.dart` |
| 19 | **Rule evaluation.** Each device evaluates rules about itself; rules about another device ("X offline") are evaluated by the freshest running computer chosen deterministically from the files, so one fires once. Each firing writes `rules/<id>/fired/<device>.jsonl` with why and what happened. | L | 18 | a "laptop offline 10 min" rule fires exactly once when the laptop sleeps, with the reason recorded | "why did this fire" view | Built (2.6.9): computers judge themselves and their things; the lowest fresh 2.6.9+ computer judges offline and phones; once per episode, deduped across evaluators by `since`; `rules/<id>/fired/<evaluator>.jsonl`, `rule.fired` events, log line `rule <id> fired ...`. Tested (fires once across two evaluators and a hand-over). Verify on the real folder once a rule exists there |
| 20 | **Notifications everywhere.** Windows toast and Linux desktop notification from the agent; Android's current opt-in alerts become rules (same behaviour, migrated). | M | 19 | the same rule notifies on the Pixel and the laptop | | Built (2.6.9): agent shows firings as a Windows toast (notification-area balloon, raw Win32, crash-guarded) or Linux desktop notification; Android shows them too; phone switches become `phone-<phone>-offline/stuck-<device>` rules, with the old phone checks kept until a 2.6.9 computer runs. Verify: a rule notifying both the Pixel and the laptop once both are on 2.6.9 |
| 21 | **Default rules** matching today's alerts plus UPS on battery, disk over 90 %, container unhealthy, DNS failing; shipped disabled except those Android already sends. | S | 20 | defaults present in a fresh folder; existing users keep their choices | | Built (2.6.9): six defaults, all off (Android's alerts are per-phone switches, now their own rules, so none needed to be on); written once by the rule-judging computer, never put back once changed or deleted. Tested |

### Phase D: the shared model (inventory, connections, physical design)

| # | Item | Size | Depends | Done when | UI it enables | Status |
|---|---|---|---|---|---|---|
| 22 | **Observed to modeled.** Core `suggestLinks()`: every device and thing in `devices/` offered as an inventory item with `observes` set; accepted ones keep their identity when telemetry changes. | M | 5 | the real UPS, MOTU, laptop and Pixel exist as items linked to their live files | "This is mine" on a device page | Built (core): `suggestLinks`/`acceptLink`, lasting identity in `match`, relink on a changed id, twins as one item. On the real folder it offers wc-tab, wc-pixel, wc-beta, the Windows laptop, wc-laptop-f and wc-desktop+wc-alpha as one machine (the laptop pair joins once both run 2.6.1+). Done when Wilson taps "This is mine" (UI) |
| 23 | **Connector and port library.** Read-only built-in library in core (`library/*.json`) plus user additions in `model/library/`. Families: USB (versions, PD, alt modes), HDMI/DP/DVI/VGA, Ethernet/fiber, AC/DC power (voltage, polarity, current), XLR/TS/TRS/TRRS/RCA/Speakon/MIDI, Eurorack power/CV/gate/audio, SATA/NVMe/PCIe. Separate shape, pinout, signal, direction, limits. Reuse knowledge from the old app's T301 planning foundation, do not re-derive it. | L | 5 | library loads on the tablet in under 100 ms; each family has tests | port pickers | Built (core): `Library.builtIn` (86 shapes, 110 signals, nine families, from T301's catalogue with pinouts and limits added) plus `model/library/*.json`. Built-in data lives in code rather than JSON assets, so it needs no asset loading on Android; loads in well under 1 ms on the build machine. Tests per family. Verify the load time on the tablet once the UI uses it |
| 24 | **Compatibility engine.** Check a chain (port, cable, adapter, port) and return a verdict per layer (physical, electrical, protocol, signal level) with evidence: verified, estimated or unknown. | L | 23 | the scenario 8 chains in `docs/SCENARIOS.md` produce the expected verdicts in tests; one real chain (laptop USB-C to the MOTU) checked | "why this works / won't" | Built (core): `checkChain` with physical/protocol/electrical/level verdicts, evidence and reasons; adapters and active converters re-check the whole path. Scenario 8's cases pass in tests (USB-C without DP Alt Mode, speaker level into an instrument input, 12 V center-positive into a 9 V center-negative pedal, HDMI to DP passive vs active). Laptop USB-C to the MOTU checked from spec declarations (USB 2.0 audio): ok. Re-check against the MOTU's live thing file when the desktop's things are readable |
| 25 | **Physical constraints.** For assemblies: fit (all dimensions, mounting standard, front/rear), weight, power budget per outlet/PSU, heat output against airflow, cable length and bend radius; each result marked verified/estimated/unknown. | L | 6, 23 | rack and pedalboard test fixtures; the "Dorm speaker" scenario's fit-but-overheats case reproduced | warnings drawn on the rack | Built (core): `checkPhysical` (fit in 3D with clearances, mount standard, 2D surfaces, Eurorack HP and rails, outlets by voltage/polarity/mA/W, heat against cooling or a closed-box rule of thumb, airflow against closed sides, cable length and bend room), each finding verified/estimated/unknown. Rack, pedalboard and Eurorack fixtures; the Dorm speaker amplifier fits its cabinet but fails on cooling clearance, heat and exhaust, as tests |
| 26 | **More control actions.** Restart a container; restart Syncthing; quiet a UPS beeper on Windows. Each an entry in `controls` and an action rules can use. | M | 19 | a container restart asked from the Pixel is applied once on the server and answered | buttons on thing pages | Won't do: Wilson 2026-11-27, monitor only (no container restarts) |
| 27 | **What's missing.** From a project: unconnected required ports, missing cables and adapters, power shortfall, open tasks, as a bill of materials with owned vs to-buy. | M | 24, 25 | the rack scenario fixture lists exactly the expected missing parts | BOM list in the project | Built (core): `projectNeeds` gives owned vs to-buy vs missing (cables named by their two shapes, adapters when a chain doesn't mate), connection problems from `checkChain`, required ports left unconnected, power/fit findings, open tasks. The scenario 2 rack fixture lists exactly the expected parts in a test |
| 28 | **Model time travel.** `ModelStore.asOf(t)` over item histories, combined with row 17 so a past moment shows both the design and the live state. | M | 5, 17 | asOf yesterday on the real folder returns yesterday's items and links | one timeline for everything | Built (core): `systemAt(root, t)` = items and projects as of t + `topologyAt` + `upAt(item)` through observes. Tested on a fixture; the real folder has no model items yet, so verify once "This is mine" (row 22 UI) has created some |

### Phase E: research and sourcing

| # | Item | Size | Depends | Done when | UI it enables | Status |
|---|---|---|---|---|---|---|
| 29 | **Location (opt-in).** Android coarse location as a reading, off by default, a switch on the device's own page. | M | | the Pixel's location appears only after switching it on | where a phone is | Built (Android, 2.7.0): reading `loc` ("lat,lon" rounded to about 1 km, last position Android already has, no GPS or extra wake-ups) only when switched on; method channel `location` / `setLocation` asks for coarse permission. Needs the switch on the device page (UI, ChatGPT); verify on the Pixel after that |
| 30 | **Product records.** `model/products/<id>.json` (specs with `src`, images, docs links, offers), linked to items and BOM lines. Manual entry first. | M | 22 | a product added on one device is linked to an item on another | product lens of an item | Built (core): `ProductStore` (versioned like items), `Product` with specs/images/docs/offers, `of(item)` / `link()`, `bestOffer()`; computers resolve its conflict copies. Tested across three device ids. Manual entry is UI (ChatGPT) |
| 31 | **Fetcher.** One collector (a server, chosen in settings) fetches offers from the sources Wilson picks, politely (rate limits, robots.txt, permitted APIs only), into `model/products/<id>/offers.jsonl`. | L | 30 | real prices for three real products refresh daily | price history | Built (2.7.1): `fetchOffers` on the computer in `model/fetch.json`, daily, eBay Browse API working with a key; Amazon and Walmart need program keys. Gate: Wilson's eBay developer key (and Amazon/Walmart keys if wanted) |
| 32 | **Price and stock tracking.** Price history, availability, product changes; rule triggers "price below", "back in stock". | M | 31, 19 | a real price change fires a rule once | price alerts | Built (2.7.3): `priceHistory`, `price.below` / `price.instock` / `price.changed` events on the crossing only (product `watch.below`), offers.jsonl compaction. Tested with a fake shop. Verify needs row 31's eBay key |
| 31a | **Exact-model front photos (#91, Wilson asked 2026-11-27).** When an item gains manufacturer + exact model, find a straight-on front view from permitted sources (manufacturer pages, media libraries, manuals), crop to the chassis with ears, keep source URL, matched model/revision and crop bounds, cache per product, leave doubtful matches for review. Exposed via monitor_core, FORMAT.md. | L | 30, 31 | MOTU 828 (2024) and CyberPower CP1500PFCRM2U get a correct cropped front view with no manual step; a similar model stays unconfirmed; reopening reuses the cache | rack faceplates show the real product, offline | Built (2.7.3): `updateFrontPhotos` on the fetching computer, daily: record's pages, then the maker's sitemaps (robots.txt obeyed); whole-word model + revision match, crop to chassis, rack-unit shape check, `front.jpg` + `front.json` confirmed/review/none, cached. Tested with a fake site. Verify on the fetching computer once `model/fetch.json` names it |
| 33 | **Discovery.** Search a source by requirement (from a project's missing parts) and propose candidates with a compatibility verdict. | L | 27, 31 | the rack scenario's missing cable gets three real candidates | "find me one" | Built (2.7.3): `requestDiscover` -> fetching computer searches per missing line, reads each listing's ends from its title, `checkChain` verdict (estimated at best), top 3 in `discover/answers/<id>.json`. Tested on the rack scenario with a fake shop. Verify needs row 31's eBay key |

### Continuous and closing items

| # | Item | Size | Depends | Done when | Status |
|---|---|---|---|---|---|
| 34 | **Docs truth.** After each phase: README, FORMAT, SECURITY, COLLABORATION section 6 match the code; nothing planned is described as built. | S each | | a read-through finds no claim the code does not back | Phase C/D pass done 2026-11-27: README gains rules, notifications and location; FORMAT, SECURITY and COLLABORATION updated with each row. Phase E pass 2026-11-28: README "Optional extras" (SMART, shops, AI); FORMAT and SECURITY gained shops, discovery and photos with their rows. Repeat after each phase |
| 35 | **Release hygiene.** Each release: APK certificate check, all five deliverables present, install steps per device in the notes. | S each | | release page complete | ongoing |
| 36 | **Retire the old .NET app's code.** | M | | repo builds and CI only covers `lite/` | Done 2026-11-27: src, tests, eng, contracts, docker, the .sln/props and the five old workflows removed (673 files); README points at lite/; old docs kept as history |
| 37 | **Optional intelligence.** Local, optional, never required: summaries of a week's events, compatibility explanations. | L | 19, 24 | runs on one server with the network off | Built (2.7.1): `ai/requests` -> `ai/answers` through a local Ollama; alpha then beta answer with their biggest model, `small` requests on the device in use. Gate: Wilson installs Ollama + a model on alpha/beta (packaging/docker/README.md "Local AI"); UI asks (ChatGPT) |

Rough total: 3 S, 23 M and 9 L items plus the recurring ones, on the
order of 20 to 25 long working sessions, most of them in phases A to D.

## 7. Keeping this file true

* The status column is updated in the same PR that changes it.
* New work Wilson asks for goes into the queue where it belongs, with a
  size and a "Done when"; it does not jump the queue unless he says so.
* A ChatGPT `data:` issue becomes a row here, placed by what it unblocks.
* Section 4 is refreshed from the real device files at the end of each long
  run.
