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
                                  "hourlyDays": 365, "dailyDays": 1825, "maxMb": 50},
                                  "update": {"auto": true}} (auto: Windows and
                                  Android install new releases on their own; default true),
                                  "controls": {"allow": ["<device id>", ...]} (who may change
                                  settings; missing = every device, [] = none; lite/SECURITY.md)
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
| `readings` | optional open-ended list: `{key, value, label?, unit?, group?, level?}`; the app shows each group as a part of the device. `level` (`warn`/`fault`) is the device's own judgement and marks the part. Computers publish groups System (maker, model, processor), Sensors (processor temperature, Linux), Tailscale (state, devices online; Linux) and Syncthing (devices connected, folders up to date, which are not), Drives (each drive when there are several) and Containers (Linux with the Docker socket readable). Computers (2.6.9+) add group Unusual when the last hour is far outside what the device usually does at that time of day: `unusual.cpu` / `unusual.mem` / `unusual.temp.cpu`, `level: warn`, value in words ("CPU 70 %, usually 8 % at night"), at most 3 new a day. Every device (computers from 2.6.8, Android too) publishes group Network each cycle: `net.gateway` (router `answers`/`no answer`; not on Windows), `net.dns` (this device's own resolver, `answers`/`no answer`), `net.dnsDirect` (1.1.1.1 or 9.9.9.9), `net.internet` (`reachable`/`no`, a TCP connect to 1.1.1.1:443 or 9.9.9.9:443), each with server and ms in `note`, and `net.ifaces` (`name address, ...`). A Docker host running Pi-hole adds `net.pihole` (`answers`/`no answer`/`stopped`, asked at 127.0.0.1:53). DNS checks ask a random name under example.com so no cache answers; NXDOMAIN counts as working. `assess()` reads these for the network status: "Internet down for everyone", "DNS failing (Pi-hole)" / "DNS failing", "Pi-hole not answering", "No internet on <device>" |

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

Detail line: `{"t":…, "b":battery%, "c":charging 0/1, "s":screen on 0/1, "cpu":%, "mem":%, "disk":%, "rx":B/s, "tx":B/s, "p":{peer id: reachable 0/1}, "w": 1 while the app is open on some device, "nt": network type (1 Wi-Fi, 2 mobile, 3 wired, 0 none), <numeric readings by key>}`

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

**Unavailable is never zero.** A reading that can't be had is written as
`value: null, src: "unknown"` with a `note`; a `0` is always a real zero
that was measured or declared. Readers must not turn null into 0 (or a bar at
0 %), and history lines simply leave unknown keys out. Sources that use 0 to
mean "don't know" are translated at the collector: UPower's 0 time to empty
and its 0 % while the state is unknown become unknown.

A reading may also carry `from`, the path it came through, where a part can
be read more than one way (today a UPS): `upower` (UPower on this machine),
`upower-export` (the host's UPower, through its exported file, in Docker),
`hid` (the device itself over USB HID), `nut`, or `sysfs`. Missing means the
device's usual path. Readings from different paths can sit side by side in
one thing (UPower's charge next to HID's load).

Software that controls a thing may write `params.json` next to its
`now.json` (any flat JSON object under 1 MB). The app shows it under "Its
settings". The CueMix rebuild is meant to use this.

Checks: the app reports under Setup > Folder health any file whose id does not
match its folder, a thing without a parent, a parent or child that does not
exist, a parent that does not list its child, a link to a missing id, or an
unknown via or dir.

Linux reads, with no vendor software: USB from `/sys/bus/usb/devices`; a UPS
through UPower first (`upower -d` natively, or in Docker the file the host's
`packaging/linux/upower-export.sh` writes, mounted read-only at
`/host/upower`), with USB HID (`/dev/hidrawN`, made readable by one udev rule)
adding load and the beeper and standing in when UPower can't be reached, and a
NUT server only if one happens to run; audio interfaces from `/proc/asound`
and `amixer`. Only UPower devices of kind `ups` are used: never a laptop's
own battery, a mouse, keyboard or the combined DisplayDevice. Each is matched
to its USB device by serial, then by USB port (following UPower's `hiddevN`
in sysfs), then by maker and model, then as the only UPS on both sides.

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
| `ups` | Linux collector: UPower (natively, or the host's exported file), plus USB HID for what UPower lacks; USB HID alone, else NUT, when UPower can't be reached | host device | `ups.status` (`mains`/`battery`/`battery, low`), `ups.charge` %, `ups.runtime` min (estimated), `ups.load` %, `ups.alarm` (`on`/`off`/`quiet until the next event`), `ups.hostAction` (declared, e.g. `host powers off at 20 %`), `ups.maker`, `ups.model`, `ups.serial` (only when it has one), `ups.identity`, `ups.source`, plus the USB set; UPS readings carry `from` |
| `audio-interface` | Linux collector | host device | `audio.card`, `audio.<stream>.state/rate/channels/format/buffer/period`, `audio.xruns` (unknown), `mixer.<name>` %, plus the USB set |
| `compose-project` | Linux collector with the Docker proxy | host device; `children` = its containers; link `via: docker` | `cp.running` ("3 of 4"), `cp.problems` (only when something needs a look) |
| `container` | same | its compose-project | `ct.state`, `ct.status`, `ct.health`, `ct.cpu` %, `ct.mem` MB, `ct.image`, `ct.name`, `ct.url` (group Interface, the link to open: a `monitor.url` label, else the first published port that answers HTTP), `ct.http` (HTTP status of one HEAD to that page each minute, or `no answer`; `warn` at 5xx or no answer) and `ct.httpMs` (its answer time). A stopped container that served a page earlier gets `ct.state` `warn` with note `was serving <url>`. A Pi-hole container also gets group Pi-hole: `ph.queries`, `ph.blocked` (today), `ph.blockedPct` %, `ph.domains`, `ph.gravityAge` (days since blocklists updated, `warn` over 14), or `ph.api` saying why there are no numbers (`needs its password`: give the collector `MONITOR_PIHOLE_PASSWORD` or `MONITOR_PIHOLE_PASSWORD_FILE`, an app password from Pi-hole's settings) |

`ups.source` says in words which paths were used ("UPower 1.91.5 on the host
(exported file), plus USB HID for load and alarm"); each UPS reading's `from`
says the same per value. UPower doesn't report the load or the beeper, so
without USB HID `ups.load` is unknown (the note says why) and no `ups.alarm`
or alarm control is written. `ups.hostAction` is what the HOST does when the
battery runs low (`host powers off at 20 % (low at 35 %, critical at 25 %)`),
read from UPower's critical action and its effective policy: `UPower.conf`,
then `UPower.conf.d/*.conf` in name order, the last file to set a key winning,
only keys in the `[UPower]` group; its note names the file that set it.
Monitor App only shows it and never changes it; shutdown stays the host's.
In Docker, an export that says UPower did not answer, or is over 3 minutes
old, makes the UPS readings unknown with that reason, never old numbers. `ups.identity` says what the thing id rests on
(`USB 0764:0601 on port 1-5 (it has no serial number)`) and how UPower's
device was matched. With no serial the id rests on the USB port, which both
systems of a dual-boot machine see alike (verified on wc-alpha / wc-desktop:
both write `usb-0764-0601-port15`); moving the UPS to another port gives it a
new id.

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


## Events (`devices/<id>/events/YYYY-MM.jsonl`)

What happened, once, on the device that saw it. One line each, UTC months,
deleted with the hourly history limit (`hourlyDays`).

```json
{"t": 1795788000, "type": "power.battery", "subject": "usb-0764-0601-port15", "name": "UPS", "detail": "mains power lost"}
```

| type | written by | when |
|---|---|---|
| `peer.offline` / `peer.online` | computers | another device's reports went stale / fresh, as this device sees them (not in the first 5 minutes after start or after this machine slept) |
| `power.battery` / `power.mains` | the computer reading the UPS | `ups.status` changed |
| `container.stopped` / `container.started` / `container.unhealthy` | Docker hosts | `ct.state` left or reached `running`; `ct.health` became `unhealthy` |
| `thing.plugged` / `thing.unplugged` | computers | an attached thing appeared or went |
| `app.updated` | every device | the collector or app came back on a new version (`detail`: "old -> new") |
| `control.applied` | every device | a request from another device was applied |

`readEvents(root, from, to, {device})` reads them all, oldest first, each
with the `device` that wrote it.

## Rules (`rules/<id>.json`)

One file per rule, so two devices never edit the same file. A rule says
when something should happen, about which devices or things, and what to do.

```json
{"v": 1, "id": "laptop-offline", "name": "Laptop offline 10 min",
 "enabled": true,
 "when": {"type": "offline", "for": 600},
 "scope": {"devices": ["301230114386"]},
 "then": [{"type": "notify"}, {"type": "annotate"}],
 "cooldown": 3600}
```

- `id`: lower-case letters, digits, `-`, `_`, `.` (at most 64), and the
  file name must be `<id>.json`.
- `when.type`:
  - `value`: `path` (e.g. `battery.pct`, `readings.ups.charge`), `op`
    (`<` `<=` `>` `>=` `==` `!=`), `value` (number, text or true/false; the
    ordering ops need a number), optional `for` (seconds it must hold).
  - `event`: `event` is an event type (`peer.offline`) or a prefix ending
    in `*` (`container.*`).
  - `unusual`: any `unusual.*` reading appears.
  - `offline`: the device's reports stop for `for` seconds (default 600).
- `scope` (optional; missing or empty means everything): `devices` (ids),
  `things` (ids), `types` (`android`, `windows`, `linux`, `thing:<kind>`).
- `then` (at least one): `notify` (optional `text`, else the reason;
  optional `on`, the device ids that show it, else every device), `annotate` (a note on
  the history at that moment), `control` (`host`, `target`, `key`, `value`:
  the same request a device could send, still subject to `controls.allow`).
- `cooldown`: seconds before the same rule fires again for the same subject
  (default 3600). `enabled`: false keeps the file but stops it.
- Unknown keys are kept when the file is rewritten.

A file that is not valid never runs. `RuleSet.load(folder)` returns the
valid rules and, per file name, the problems in plain sentences
("\"when.op\" < needs a number"), so an editor can show them. Writers use
`RuleSet.save`, which validates and writes atomically.

**Who runs a rule (2.6.9+).** A computer judges value, event and unusual
rules about itself and the things it hosts. Offline rules, and rules about
phones, are judged by one computer: the lowest id among the fresh computers
on 2.6.9 or newer, which every computer works out the same way from the
files. A condition fires once per episode (after holding `for` seconds;
again only after it stopped holding and `cooldown` passed). Offline never
fires for one system of a dual-boot machine while the other runs, and no
rule runs in the first 5 minutes after a start or a sleep. Events of type
`rule.*` never trigger rules.

**What fired: `rules/<id>/fired/<evaluator>.jsonl`**, one line per firing,
written only by the evaluator named in the file name (at most 256 KB; the
older half is dropped):

```json
{"t": 1764252000, "rule": "laptop-offline", "subject": "301230114386",
 "since": 1764251400, "why": "Wilsons-Laptop: no report for 10 min (last 13:50Z)",
 "did": ["notify", "annotate"], "notify": "Laptop offline 10 min: ...", "by": "ebad35097c54"}
```

`since` is when the episode began; a firing whose `since` is already in any
evaluator's file is not repeated, so a hand-over between evaluators can't
fire twice. `annotate` adds a `rule.fired` event (subject, name = rule name,
detail = why) to the evaluator's events. `control` sends an ordinary request
from the evaluator. `notify` is recorded here with the rule's `name` and `on`.

**Notifications (2.6.9+).** Every device reads the firing files and shows
each new firing meant for it once (title = rule name, text = `notify`):
Windows as a toast, a Linux desktop through its notification service,
Android as a notification. A firing that reaches a device over an hour
late is skipped; at most three show at once, then a count. Servers
without a desktop show nothing.

**Default rules (2.6.9+).** The computer that judges rules for others
writes these once per folder, all with `enabled: false`: `device-offline`
(30 min), `ups-on-battery` (`power.battery` event), `disk-over-90`
(`disk.pct`, 10 min), `container-unhealthy`, `dns-failing`
(`readings.net.dns` is "no answer" for 5 min) and `syncthing-stuck`. Which
ones were written is kept in `rules/.defaults-written.json`, so a default
someone changed or deleted is never put back. `disk.pct` and `mem.pct` are
worked out from `used` and `total` for any rule.

**Phone switches.** A phone's "Notify me on this phone" switch for a device
is kept as two rules notifying only that phone:
`phone-<phone>-offline-<device>.json` (no report for 30 min) and
`phone-<phone>-stuck-<device>.json` (`readings.st.problems` not empty for
15 min). Switching off sets `enabled` false. Until a 2.6.9+ computer is
running to judge rules, the phone keeps checking these itself.

## Model: things you own, plan or observe (`model/`)

Telemetry (`devices/`) says what a device measures; the model says what
things *are*: the UPS you own, the rack you are planning, the cable you
still need. Any device may write it, so it has its own rules.

```
model/items/<id>/item.json              the item now
model/items/<id>/history/<device>.jsonl every version, appended only by that device
```

```json
{"v": 1, "id": "ups-1", "type": "ups", "name": "CyberPower OR500",
 "state": "owned", "parent": "rack-1",
 "props": {"height": {"value": 1, "unit": "U", "src": "declared"},
           "load": {"value": 13, "unit": "%", "src": "measured"}},
 "ports": [{"id": "out1", "kind": "nema-5-15", "dir": "out"}],
 "links": [{"to": "desktop-1", "port": "out1", "via": "power"}],
 "observes": "usb-0764-0601-port15",
 "rev": 3, "by": "301230114386", "t": 1795788000.123}
```

* `id`: lowercase letters, digits, `-`, `_`, `.`; at most 64; never starts
  with a dot. Readers refuse anything else.
* `state`: `proposed`, `owned`, `installed` or `retired`. Items are never
  deleted, only retired.
* `props`: every value carries `src` (`measured`, `declared`, `estimated`,
  `unknown`) and optionally `unit`.
* `observes`: the device or thing id in `devices/` whose telemetry this item is.
* `match` (items made from what was seen): the object's lasting identity,
  `usb:<vid:pid>:<serial>` (else `usb:<vid:pid>@<host>:<port>`),
  `machine:<machine key>`, `device:<id>` or `thing:<id>`. When the
  telemetry id changes (a UPS without a serial moved to another port), the
  item is offered a relink, not a duplicate.
* `alsoObserves`: the other systems of a dual-boot machine.
* `suggestLinks(reports, things, items, now)` lists devices (one per
  machine) and things not yet in the model; `acceptLink(store, s)` makes an
  `owned` item under its host's item, or relinks. `tool/suggest.dart`
  prints them for a folder.
* `rev`, `by`, `t`: revision, the device that wrote it, when (Unix seconds).
* Unknown keys are kept on every edit.
* Written to `.syncthing.item.json.tmp` and renamed.
* Two devices writing at once leave `item.sync-conflict-*.json`. Readers take
  the newest `t` (then `rev`, then `by`) of all copies. Computers'
  collectors resolve copies every 6 hours: the newest becomes `item.json`,
  each loser is noted in history (`"conflictLoser": true`) and the copy is
  deleted. Folder health lists copies until then.
* `ModelStore(root, deviceId)`: `list()`, `get(id)`, `put(item)`,
  `retire(id)`, `history(id)`, `asOf(t)`, `conflicts()`, `resolveConflicts()`.

## Projects and assemblies (`model/projects/`)

Same versioning, history, conflict and time-travel rules as items
(`model/projects/<id>/project.json`, `history/<device>.jsonl`).

```json
{"v": 1, "id": "studio-rack", "name": "Studio rack", "goal": "...",
 "state": "draft", "items": ["ups-1"],
 "tasks": [{"id": "t1", "text": "Measure the desk depth", "done": false}],
 "assemblies": [{"id": "rack", "name": "Rack", "type": "rack", "units": 12,
   "depth": {"value": 450, "unit": "mm", "src": "declared"},
   "maxWeight": {"value": 40, "unit": "kg", "src": "declared"},
   "placements": [{"id": "p1", "name": "UPS", "thing": "usb-0764-0601-port15",
     "u": [1, 2], "face": "front", "depth": {"value": 14, "unit": "in", "src": "declared"},
     "weight": {"value": 6, "unit": "kg", "src": "declared"}}]}],
 "rev": 1, "by": "301230114386", "t": 1795788000.5}
```

* `state`: `draft`, `active`, `done`, `archived` (never deleted).
* A placement points at a model `item`, a live `thing`/device id, or neither
  (a planned part with only a name). `u`: `[bottom, top]` rack units, or
  `null` while not placed. `height`: rack units it takes, kept while it is
  not placed (a 2U amplifier with no position yet); `check()` flags a
  placement whose `u` range doesn't match it. `face`: `front` or `rear`; `fullDepth: true`
  blocks both faces.
* Quantities are `{"value", "unit", "src"}`. Units understood: mm, cm, m,
  in, ft; g, kg, lb, oz; mW, W, kW. Others are kept but not added up.
* `Assembly.check()`: overlaps (same face), outside the rack, deeper than
  the rack, over the weight or power limit, as sentences; weight and power
  totals with how many parts had a value (`Total.known` of `count`); free units.
* `ProjectStore(root, deviceId)`: `list()`, `get`, `put`, `archive`,
  `history`, `asOf`, `conflicts()`, `resolveConflicts()`.

## Library: connectors and signals (`model/library/*.json`)

The built-in library is in the code (`Library.builtIn`), so the phone
loads it instantly: 86 connector shapes and 110 signals in the families
usb, display, network, power, audio, eurorack, storage, rf and control. It
comes from the old app's planning catalogue, with pinouts and limits added.
It keeps four things apart:

- **shape** (`connectors[]`): `fit` says what mates (a plug goes into a
  receptacle with the same `fit`, or one listed in the receptacle's
  `alsoMates`); `genders`; `pins` when the standard fixes them.
- **signals the shape may carry** (`signals`): never a promise. A USB-C
  shape guarantees USB 2 and 5 V; Thunderbolt or DisplayPort only when the
  port and the cable declare it.
- **signal** (`signals[]`): `kind` (data, video, audio, power, control),
  `direction` (`both` or `out-to-in`), `limits` from the standard (`gbps`,
  `v`, `vMin`, `a`, `w`, `dbu`, `impedanceOhm`, `polarity`, `m`).

Your own additions go in `model/library/<name>.json`:

```json
{"signals": [{"id": "motu-avb", "label": "MOTU AVB", "family": "network", "kind": "audio"}],
 "connectors": [{"id": "motu-combo", "label": "Combo XLR/TRS", "family": "audio",
   "fit": "xlr3", "alsoMates": ["jack-635"], "signals": ["mic-level", "line-pro"]}]}
```

Ids follow the model id rule. A file may not replace a built-in id; a bad
file is listed in `Library.load(root).problems` and skipped.

### Checking a connection (`checkChain`)

`checkChain(from, links, to, {want})` checks a whole path, port to cable or
adapter to port, and returns a verdict for each layer:

- `physical`: every joint mates, by shape and gender;
- `protocol`: a data, video or control signal gets through every part, or
  the one asked for with `want`;
- `electrical`: the source's voltage, polarity and current suit the sink
  and the cable;
- `level`: audio leaving one end is a level the other end takes.

Each layer says `ok`, `fail` or `unknown`, and how sure it is:

- `verified`: both ends declare their signals;
- `estimated`: from what the shapes may carry;
- `unknown`.

Every verdict comes with its reasons in words. A `Port` declares its
connector, `dir` (`out`, `in` or `both`), `signals` and `power` (`v`,
`vMin`, `a`, `polarity`). A `Link` (a cable or adapter) declares the
connectors at its ends, `passes` (the signals it carries unchanged) or
`converts` (for an active adapter), and `maxA` / `maxGbps`.

### Physical constraints (`checkPhysical`)

`checkPhysical(assembly)` returns a list of findings. Each finding has a
`topic` (`fit`, `mount`, `power`, `heat` or `cable`), a `level` (`fail`,
`warn`, `ok` or `unknown`) and an `evidence`:

- `verified`: from declared or measured values;
- `estimated`: a rule of thumb was used, for example for heat in a closed
  box;
- `unknown`: the finding names the value that is missing.

All the fields below are optional, and older files stay valid.

**On the assembly:**

| Field | Meaning |
|---|---|
| `mount` | `19in`, `10in`, `eurorack` ... |
| `width`, `heightMm`, `depth` | Inside size, as Qty |
| `ventilation` | `open`, `vented` or `closed` |
| `cooling` | Heat it can remove, as a W Qty |
| `rearOpen`, `frontOpen`, `topOpen`, `sidesOpen` | Whether that side is open |
| `outlets` | List of `{id, v, polarity, ma, maxPower}` |
| `hp`, `psu` | Eurorack row width; `psu` is `{"+12": mA, "-12": mA, "+5": mA}` |
| `cables` | List of `{name, from, to, length, minBend}` |

**On a placement:**

| Field | Meaning |
|---|---|
| `mount` | Its mounting standard |
| `width`, `heightMm` | Its size, as Qty (depth is the usual `depth`) |
| `x`, `y` | Position on a flat surface |
| `clearance` | `{front, rear, top, sides}` |
| `airflow` | `front-to-back`, `back-to-front`, `side-to-side`, `bottom-to-top` or `passive` |
| `heat` | Heat it gives off; if missing, power × (1 − `efficiency`), else all of its power, both estimated |
| `outlet`, `v`, `polarity`, `ma` | Which outlet it uses and what it needs |
| `hp`, `row`, `ma` | Eurorack; here `ma` is `{"+12": ...}` |

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
