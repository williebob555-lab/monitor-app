# Monitor App 2.x security

This describes what 2.x (the `lite/` app) actually does. The 1.x model is in
`docs/SECURITY.md`.

## What protects what

| Layer | Protects | Doesn't protect |
|---|---|---|
| **Syncthing** | Carries the shared folder between your devices, encrypted (TLS) and only between devices you added to each other by device ID. Nobody else can read or write it. | Anything once a device is added: every device in the folder can read all of it and write anywhere in it. Removing a device in Syncthing is how you revoke it. |
| **Tailscale** | Used only to check that devices are alive (ping, a TCP connect to Syncthing's port 22000). | Monitor App sends no data over it and opens no port of its own. |
| **The folder rules below** | Keep a confused or broken device from harming the others through the files it writes. | A device you added on purpose that is fully compromised can still write misleading readings into its own folder. |

Monitor App listens on no network port on any device. The Docker collector
uses host networking and publishes nothing.

## What a reader accepts from the folder

Each device writes only inside `devices/<its id>/`, the shared `model/`
store, `rules/<id>.json` when someone edits a rule, and its own
`rules/<id>/fired/<its id>.jsonl`. Syncthing doesn't enforce this, so readers check:

- **Identity.** A `now.json` counts only if its `id` matches its folder
  name, so one device can't pose as another.
- **Size.** `now.json` over 1 MB, or a request or result over 16 KB, is
  ignored.
- **Paths.** Nothing read from a file becomes a path unless it passes a
  strict pattern:
  - request and result ids are hex;
  - device and thing ids are letters, digits, `.`, `_` and `-`, with no `..`;
  - model ids follow `^[a-z0-9_-][a-z0-9._-]{0,63}$`.
- **Conflict copies.** `.sync-conflict-` files are never read as data.
  - In `model/`, the newest version wins and the losing copies are kept in
    the history.
  - In device folders, they are removed.
- **Half-written files.** Writers write to a temporary file and rename it.
  A reader that gets a half-synced file keeps the previous one.

## Changing settings from another device

- A device offers only the controls it declares in its own `now.json`
  (`controls`). Today those are the UPS beeper (`ups.alarm`) and `update`.
- No control runs a command that was given to it. Each one is a fixed
  action inside the collector.
- A request is applied only if all of these hold:
  - it is in the asking device's own folder, with `from` equal to that
    folder's name;
  - it is for this device (`host`);
  - it is at most 10 minutes old;
  - its key is a known control.
- `settings.json` can limit who may ask: `"controls": {"allow": ["<device id>", ...]}`.
  - Missing (the default): every device in the folder may ask.
  - An empty list: no device may ask.
- Every request is answered with a result file. The device that applies
  it also writes it to its own log and, when applied, records a
  `control.applied` event.
- No biometrics or PIN are ever required. The app asks in-app before
  sending.

## Rules

- A rule can only notify, add a note to history, or send the same control
  request a person could send. A rule's control request comes from the
  device that judged the rule and still has to pass `controls.allow` on the
  device that applies it.
- Rule ids follow the model id pattern before they become a file or folder
  name. A rule file over 64 KB, or one with any problem, never runs.

## Updates

- New versions come only from this repository's GitHub releases (`lite-v*`).
- Every download is checked against the release's `SHA256SUMS` before it is
  installed. This catches a broken or swapped download. It does not protect
  against someone who controls the GitHub account, because the sums are
  published by the same release.
- Android installs only an APK signed with the same key as the installed
  app, and the system enforces this. The signing key never leaves Wilson's
  computer.
- Linux packages are installed by the root timer with `dnf` or `apt` from
  the verified file.
- Docker servers rebuild through `update.sh`. The script is
  deployment-aware and honours a version pin.
- `settings.json` `"update": {"auto": false}` stops automatic installs on
  every device.

## Secrets

Each secret stays on its own device and is never written to the shared
folder:

- **Syncthing API key:** read from Syncthing's own config on the same
  computer.
- **Pi-hole password:** only from the collector's environment
  (`MONITOR_PIHOLE_PASSWORD` or `_FILE`).
- **Tailscale:** read through its local socket or named pipe, which needs
  no key.
- **Shop API keys** (offers, 2.7.1): only in the fetching computer's own
  `keys.json` or its environment. The shared folder holds only the offers.

## Docker collector

- Read-only access to Docker through a socket proxy that allows only `GET`
  on containers.
- The UPS is read through one `hidraw` device mapping. The beeper can be
  set only through that device's HID alarm control.
- Its own state lives outside the project folder. It never touches other
  containers.

## Known limits

- Anyone with a device in the Syncthing folder can read all telemetry,
  including device names, addresses on Tailscale and the local network,
  and container names.
- A phone's location, when its owner switches it on (off by default), is
  rounded to about 1 km and readable by every device in the folder.
  The switch is on that phone's own page and discloses the shared audience
  before requesting Android permission.
- A device that is fully compromised can still write wrong readings about
  itself, and ask for allowed controls.
- Model, project and event files are trusted as data from the devices in
  the folder. They are validated for shape and paths, not for truth.
