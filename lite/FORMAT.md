# Shared folder format (version 1)

```
<Monitor folder>/                 any folder Syncthing shares, or a subfolder of one
                                  (e.g. Infrastructure/Monitor)
  devices/<device id>/
    now.json                      latest report, replaced every cycle
    history/detail/YYYY-MM-DD-HH.jsonl   every reading, one file per hour
    history/hourly/YYYY-MM.jsonl         one summary line per hour
    history/daily/YYYY.jsonl             one summary line per day
    history/patterns/YYYY.jsonl          one line of pattern values per day
    log.txt                       the app's own log, kept under 256 KB
    patterns.json                 what its history shows, in words (every 6 h)
    things/<thing id>/            things it hosts: USB UPS, audio interface, Docker
      now.json                    projects and containers (same shape as a device's)
      history/ , params.json      its own history; optional settings from its software
    requests/<id>.json            changes this device asked another device to make
    results/<id>.json             this device's answers to requests it applied
  watch/<device id>.json          {"id","name","until"}: that device's app is open
                                  until "until" (Unix seconds); collectors go live
  settings.json                   shared settings, e.g. {"history": {"detailDays": 7,
                                  "hourlyDays": 365, "dailyDays": 1825, "maxMb": 50}}
```

A device writes only inside its own `devices/<id>/`, so Syncthing never sees
two writers for one file. On computers `now.json` is written to `.syncthing.now.json.tmp`
(Syncthing's own temp pattern, which it never syncs) and renamed. On Android it
is written in place and its modification time set explicitly: there a renamed
file kept its first write's time, and Syncthing (which compares time and size)
skipped every update of the same size.

The folder is the database: plain files a person can open. Later kinds of
things (non-primary devices, IoT) get their own top-level folder next to
`devices/`, one folder per thing, in the same style. Nothing reads them yet.

## now.json

| Key | Meaning |
|---|---|
| `v` | format version (1) |
| `id`, `name`, `kind` | device id, display name, `windows`/`linux`/`android` |
| `t`, `seq`, `next` | write time (Unix seconds), counter, seconds until the next write |
| `os`, `app`, `machine`, `boot` | system name, app version, hardware key (same for every OS on a dual-boot machine), boot time |
| `battery` | `pct`, `charging`, `plugged`, `tempC` |
| `cpu.pct` / `cpuNote` | whole-device CPU %, or why it is missing (Android) |
| `mem`, `disk` | `used`, `total` bytes |
| `net` | `rxBps`, `txBps`, `type` (`wifi`/`ethernet`/`cellular`/`none`) |
| `ts.ip` | Tailscale address or null |
| `st.up` | Syncthing listening on this device (port 22000) |
| `screen` | `on`/`off` (Android) |
| `peers.<id>` | what this device sees of another: `tcp` (`open`/`refused`/`timeout`/`noip`/`error`), `ping` ms (ICMP), `tcpMs` (computers: TCP handshake time to its port 22000, open or refused; present where ICMP is not allowed, e.g. Docker), `seen` = age in seconds of that device's now.json here |
| `self` | collector cost: `runMs`, `cpuMs` (the collector's own CPU; on Android its collection thread, not the app screen sharing the process), `rssMb`, `over` (broken budgets) |
| `node` | its place in the hierarchy (see "Hierarchy") |
| `controls` | settings anyone may ask it to change (see "Controls") |
| `connected` | things only: `true`, or `false` once unplugged / gone |
| `watch` | names of the devices that have the app open, when written at the live rate |
| `readings` | optional open-ended list: `{key, value, label?, unit?, group?, level?}`; the app shows each group as a part of the device. `level` (`warn`/`fault`) is the device's own judgement and marks the part. Computers publish groups System (maker, model, processor), Sensors (processor temperature, Linux), Tailscale (state, devices online; Linux) and Syncthing (devices connected, folders up to date, which are not), Drives (each drive when there are several) and Containers (Linux with the Docker socket readable) |

Missing keys mean "not available"; readers never guess.

## Built to grow (rules every reader and writer follows)

* **Unknown fields are kept, never an error.** A reader keeps the whole file
  (`Report.raw`) and uses the fields it knows. A newer device can add fields
  and older apps still read it.
* **`v` only goes up for breaking changes.** Adding fields never bumps it.
  Readers accept any `v` and read what they recognise.
* **Every value has a path.** `battery.pct`, `net.type`, `peers.<id>.tcp`,
  `readings.<key>` (extra readings are addressed by their `key`, and
  `readings.<key>.unit` gives the unit). `Report.field(path)` /
  `Report.number(path)` pull any of them out, `Report.flatten()` lists them all,
  and `seriesOf(historyLines, path)` turns any numeric history key into a chart.
  The device page has an "All fields" view with a filter.
* **New kinds of data go in `readings`** first (`{key, value, label, unit, group}`,
  keys dotted like `temp.cpu` or `svc.syncthing`). The app shows them grouped,
  and numeric ones get a history chart, without an app change.
* **Bad values are skipped one by one.** A wrong type in one field doesn't
  discard the rest of the file.

A report written at the live rate has `"next": 10` (or 15 on Android) and
`"watch": ["<name of the device looking>", ...]`.

## History

Detail line: `{"t":…, "b":battery%, "c":charging 0/1, "s":screen on 0/1, "cpu":%, "mem":%, "disk":%, "rx":B/s, "tx":B/s, "p":{peer id: reachable 0/1}, <numeric readings by key>}`

Summary line (hourly and daily): `{"t": start, "n": readings, "<path>": [min, avg, max], ...}`
for every numeric value, nested ones flattened (`"p.<peer id>"`).

How it stays small:
* Readings go to a local, unsynced spool first and move into the hour's detail
  file every 15 minutes, so Syncthing sees a few small changes an hour, never a
  growing file rewritten every minute. An hour file stays around 10-15 KB.
* Ended hours and days are summarised by the device that owns them.
* Each device deletes its own old files by the limits in `settings.json`
  (shown and editable under Setup > History), then enforces the size cap
  oldest-detail-first. Defaults: every reading 7 days, hourly 1 year, daily 5 years, 50 MB.
* File names use the **writing device's local time** (a server in Docker is
  UTC unless it mounts the host's `/etc/localtime`). Readers therefore look at
  every name the period could have on any clock (UTC-12 to UTC+14) and keep
  lines by `t`: at most about 53 hour names for the 24 h view (most don't
  exist), the hourly file(s) for 7 days, the daily file(s) for a year, so
  reading cost doesn't grow with history. Use `readHistoryRange`,
  `readDetail`, `readHourly`; never build the names from your own clock.

## Hierarchy: `node`, attached things and `params.json`

Every `now.json` (2.5.0 and later) declares its place in a `node` block.
The map and the app draw the hierarchy from these declarations only; nothing
is guessed.

```json
"node": {
  "id": "de5c70a1fa00",            // same as the file's id
  "type": "device",                 // device | ups | audio-interface | ...
  "parent": null,                   // id of what it hangs off, null at the top
  "children": ["usb-0764-0601-cxxlv2004567"],
  "links": [{"to": "usb-0764-0601-cxxlv2004567", "via": "power", "dir": "in", "detail": "mains or battery power"}]
}
```

* `via` is one of usb, power, network, audio, thunderbolt, serial, bluetooth,
  dual-boot (two systems of one machine), docker (a Compose project on its host).
* `dir` is in, out or both, seen from the file that declares it.
* Network reachability stays in `peers` (measured); `links` holds physical
  connections.

Things plugged into a device are written by that device to
`devices/<host>/things/<thing id>/now.json`. Their `kind` is `thing`, they
carry `connected: true|false`, and they keep their own `history/`. A thing's
id is stable across reboots and across both systems of a dual-boot machine:
`usb-<vendor>-<product>-<serial>` (or the port when there is no serial). When
two systems wrote the same thing, the newer file wins. An unplugged thing is
rewritten once as `connected: false` and stays in the hierarchy.

Every reading may carry `src` (measured, declared, estimated or unknown) and a
`note`. `value` may be null only when `src` is unknown, and the note then says
why (for example "USB ports do not measure current").

Software that controls a thing may write `params.json` next to its
`now.json` (any flat JSON object under 1 MB). The app shows it under "Its
settings". The CueMix rebuild is meant to use this.

Checks: the app reports under Setup > Folder health any file whose id does not
match its folder, a thing without a parent, a parent or child that does not
exist, a parent that does not list its child, a link to a missing id, or an
unknown via or dir.

Linux reads, with no vendor software: USB from `/sys/bus/usb/devices`; a UPS
directly as a USB HID power device (`/dev/hidrawN`, made readable by one udev
rule; NUT or upower only as a fallback if present), giving just its basic
status: on mains or battery, charge, load, runtime; audio interfaces from
`/proc/asound` and `amixer`.

## Dual-boot twins

Two systems on one physical machine (wc-alpha / wc-desktop, Wilsons-Laptop /
wc-laptop-f) are two devices with two ids, two folders and the same `machine`
key (the lowest burned-in wired or Wi-Fi MAC; Android has none). Each computer
declares `{"to": <twin id>, "via": "dual-boot", "dir": "both"}` in its
`node.links`. Only one runs at a time, so the twin with the older `t` is not
booted: core `assess()` gives it `Level.inactive` ("This machine is running X
right now"), not a fault, and peers do not probe it. The UI should draw twins
as one machine with the running system on top.

## Things written today

| `node.type` | Written by | Parent | Readings (all `src`-marked) |
|---|---|---|---|
| `ups` | Linux collector: USB HID, else NUT, else UPower (`upower` or the host's exported file, packaging/linux/upower-export.sh) | host device | `ups.status` (`mains`/`battery`/`battery, low`), `ups.charge` %, `ups.runtime` min (estimated), `ups.load` %, `ups.alarm` (`on`/`off`/`quiet until the next event`), `ups.maker`, `ups.model`, `ups.source`, plus the USB set |
| `audio-interface` | Linux collector | host device | `audio.card`, `audio.<stream>.state/rate/channels/format/buffer/period`, `audio.xruns` (unknown), `mixer.<name>` %, plus the USB set |
| `compose-project` | Linux collector with the Docker proxy | host device; `children` = its containers; link `via: docker` | `cp.running` ("3 of 4"), `cp.problems` (only when something needs a look) |
| `container` | same | its compose-project | `ct.state`, `ct.status`, `ct.health`, `ct.cpu` %, `ct.mem` MB, `ct.image`, `ct.name`, `ct.url` (group Interface, the link to open: a `monitor.url` label, else the first published port that answers HTTP) |

`ups.source` says which path was used. Through UPower, `ups.load` is unknown
and no `ups.alarm` (or control) is written.

USB set: `usb.speed` Mbit/s, `usb.version`, `usb.port`, `usb.maxPower` mA
(declared), `usb.current` and `usb.errors` (always unknown, with a note),
`usb.connectedMin`, `usb.reconnects`, `usb.serial`, `usb.id`.
UPS and audio things link to the host `via: usb` (a UPS also `via: power`,
`dir: out`). A container that disappears is rewritten once with
`connected: false`. Windows and Android write no things yet.

## Controls (change a setting from any device)

A device or thing that offers a change declares it in its own `now.json`:

```json
"controls": [{"key": "ups.alarm", "label": "Alarm", "options": ["on", "off", "quiet"],
              "value": "on", "note": "...", "confirm": "what to say before sending"}]
```

One option means an action (a button); several mean a choice. Declared today:
`ups.alarm` on a UPS thing (only when the UPS reports the setting), and
`update` (one option, `value` = installed version) on Windows (installs the
newest release silently) and Android (posts a notification; Android installs
only with a tap). Linux and Docker declare no `update`: they update by hand or
by their 15-minute timer.

To ask, a device writes into **its own** folder
`devices/<asker>/requests/<id>.json`:
`{"id", "from", "fromName", "host", "target", "key", "value", "t"}`
(`host` = the device that applies it, `target` = that device or one of its
things, `id` = 24 random hex characters). The host applies each request at
most once and only within 10 minutes of `t` (either way). Only keys it can
apply are done (`ups.alarm` on Linux, `update` on Windows and Android); anything else is
answered `"ok": false`. It logs "control key=value for target, asked by fromName: message", and
answers in `devices/<host>/results/<id>.json`: `{"id", "ok", "message", "value"?, "t"}`.
No answer after 10 minutes means it lapsed (host off or not synced). Both
folders are pruned after a day. In Dart: `sendRequest`, `myRequests`,
`resultOf` (the app side) and `requestsFor`, `handled`, `writeResult`,
`pruneControls` (the collector side), in `control.dart`.

## Patterns

What a device's history shows, in words, worked out from the history files
(core `patterns.dart`), so any device can show any other device's patterns:

* Battery: drain per hour with the screen on and off (or on battery), charge
  per hour, and how long a full charge would last at that rate (estimated).
  Only readings at most 20 minutes apart under the same condition count; a
  battery held at 100 % "not charging" is a full one on mains, not drain.
* CPU: the busiest and quietest part of the day (night, morning, afternoon,
  evening, local time), or "about N % all day".
* Memory and CPU temperature: the usual level (median of hourly averages)
  and the highest seen.
* Network: data down and up per day.
* Connections: how often it reached each other device, counting only hours
  that device was itself reporting (one switched off is not a connection
  problem).
* UPS (a `ups` thing, written next to its own `now.json` in
  `things/<id>/patterns.json` with its own `history/patterns/`): power cuts
  ridden through in the last 7 days, with minutes on battery and the longest
  (`ups.cuts`, or "No power cuts"); the usual and highest load
  (`ups.load.usual`); and the runtime the UPS expects when full on mains
  (`ups.runtime.full`, estimated: falling over months is the battery wearing
  out). Cuts are counted from `ups.onBattery` (1 on battery, 0 on mains),
  which a UPS thing's history lines carry from 2.6.7.

Each pattern says how many hours of history it rests on, and nothing resting
on under an hour (six for daily shapes) is shown. Every device (computers in
core `patterns.dart`, phones and tablets in `Patterns.kt`, same rules) keeps
the result in `devices/<id>/patterns.json` (rewritten every 6 hours):

```json
{"v": 1, "t": 1795740000, "patterns": [
  {"key": "battery.drain.off", "group": "Battery", "text": "Drains 4.1 %/h with the screen off (over 4.9 h)",
   "value": 4.1, "unit": "%/h", "hours": 4.9}]}
```

At the same time it writes today's line of `history/patterns/YYYY.jsonl`:
`{"t": local day start, "<pattern key>": value, ...}` (one line per day, the
day's last run wins). It is pruned with the daily summaries (`dailyDays`) and
goes last under the size cap, so patterns outlast the detail and hourly
files they came from. Read it with `readPatternHistory(deviceDir, now)`;
`seriesOf(lines, "battery.drain.off")` charts one pattern over months.


## Reading the folder from Dart (`package:monitor_core`)

The app reads everything through core; it never parses files itself.

| Need | Call |
|---|---|
| Open the folder | `SyncFolder(root)`; `exists`, `sharedBySyncthing` |
| Every device's latest report | `readReports()` -> `Map<id, Report>` (a half-synced file keeps showing the previous report; files over 1 MB are skipped) |
| Things | `readThings()` -> `Map<id, Thing>` (`Thing.report`, `.host`, `.params`) |
| Status in words | `assess(reports, myId:)` -> `Assessment` (`devices`: `DeviceStatus` with `level` ok/inactive/warn/fault, `title`, `detail`, `fresh`, `age`; `network`: `NetworkStatus` with split `groups`); `thingStatus(thing)`; `freshLimit(r)`, `ghostAfter` (3 days) |
| Any value | `Report.field('battery.pct')`, `.number(path)`, `.flatten()`, `.readings`, `.node`, `.controls`, `.raw` |
| History | `readHistoryRange(deviceDir, HistoryRange.day/week/year, now)`, `seriesOf(lines, path)`, `chartValue` |
| Patterns | `findPatterns(readDetail(dir, now), readHourly(dir, now), names:, upHours:)` (heavy: run off the UI thread) |
| Hierarchy checks | `checkTopology(devices, things)`; `health()` -> `FolderHealth` |
| Live rate | `writeWatch(myId, myName, now)` every `watchIntervalSeconds` while visible, `clearWatch` when hidden; `watchers(now)` |
| Log | `readLog(id)` |
| Settings | `Retention.load(root)` / `.save(root)` |

## What stays stable

* Paths, file names and every key listed in this file keep their meaning.
  Fields and reading keys are only added, never renamed or removed without a
  `v` bump, and a `v` bump is announced in `docs/COLLABORATION.md` first.
* The public `monitor_core` API above keeps its names; additions are fine.
  A breaking change is done by the backend owner, with the app updated in the
  same PR.
* This file describes only what is written today. A planned field is not
  listed here until a collector writes it.
