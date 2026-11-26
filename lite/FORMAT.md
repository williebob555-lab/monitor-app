# Shared folder format (version 1)

```
<Monitor folder>/                 any folder Syncthing shares, or a subfolder of one
                                  (e.g. Infrastructure/Monitor)
  devices/<device id>/
    now.json                      latest report, replaced every cycle
    history/detail/YYYY-MM-DD-HH.jsonl   every reading, one file per hour
    history/hourly/YYYY-MM.jsonl         one summary line per hour
    history/daily/YYYY.jsonl             one summary line per day
    log.txt                       the app's own log, kept under 256 KB
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
| `peers.<id>` | what this device sees of another: `tcp` (`open`/`refused`/`timeout`/`noip`/`error`), `ping` ms, `seen` = age in seconds of that device's now.json here |
| `self` | collector cost: `runMs`, `cpuMs`, `rssMb`, `over` (broken budgets) |
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
* Readers open at most 25 hour files (24 h view), the hourly file(s) (7 days)
  or the daily file(s) (year), so reading cost doesn't grow with history.

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

* `via` is one of usb, power, network, audio, thunderbolt, serial, bluetooth.
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
through NUT's network protocol on 127.0.0.1:3493 if NUT runs, else `upower`;
audio interfaces from `/proc/asound` and `amixer`.

## Planned: patterns instead of raw data

Long-term, raw numbers matter less than what they show ("battery drains
4 %/h on the wireless pad", "CPU averages 30 % on weekday evenings"). The
plan is one readable `devices/<id>/patterns.json` per device, built from the
hourly and daily summaries, grouped by conditions already recorded in every
line (charging `c`, screen `s`, hour of day, weekday, network type). Once
patterns exist, older summaries can be cut back further. Not built yet.
