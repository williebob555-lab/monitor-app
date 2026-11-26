# Shared folder format (version 1)

```
<Monitor folder>/                 any folder Syncthing shares, or a subfolder of one
                                  (e.g. Infrastructure/Monitor)
  devices/<device id>/
    now.json                      latest report, replaced every cycle
    history-YYYY-MM-DD.jsonl      one compact line per cycle, 7 days kept
    log.txt                       the app's own log, kept under 256 KB
```

A device writes only inside its own `devices/<id>/`, so Syncthing never sees
two writers for one file. `now.json` is written to `.syncthing.now.json.tmp`
(Syncthing's own temp pattern, which it never syncs) and renamed.

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
| `readings` | optional open-ended list: `{key, value, label?, unit?, group?}`; the app shows any it finds, grouped |

Missing keys mean "not available"; readers never guess.

## history line

`{"t":…, "b":battery%, "c":charging 0/1, "cpu":%, "mem":%, "disk":%, "rx":B/s, "tx":B/s, "p":{peer id: reachable 0/1}, <numeric readings by key>}`
