# Monitor App (lightweight rebuild)

A small app that shows the health of every one of your devices: battery, CPU,
memory, disk and network, plus a plain-English status ("Online",
"Syncthing stopped", "Offline", "Network split").

It has no server, no database, no pairing and no networking code of its own:

* **Syncthing** moves the data. Each device writes its own small files into one
  shared folder, and Syncthing copies them directly between your devices. Phones
  and tablets sync with each other with no computer on.
* **Tailscale** answers "is it alive?". Each device checks the others' Syncthing
  port over Tailscale and writes down what it saw. Comparing what every device
  sees tells a device that is off apart from one whose Syncthing stopped, and
  spots a split network.

## Set up a device (once)

1. Install it (see the release's "Which file do I need?" table).
2. Point the app at a folder Syncthing shares between your devices. Either a
   subfolder of a folder you already share (e.g. `Infrastructure/Monitor`; set it
   under Setup > Monitor folder), or a new Syncthing folder with
   **Folder ID `monitor-app`**. The default path is
   shown in the app under Setup (Windows: `C:\Monitor App\Sync` if `C:\Monitor App`
   exists, else `%USERPROFILE%\Monitor`; Linux: `~/Monitor`; Android:
   `/storage/emulated/0/Monitor`). Share it with your other devices.
3. Android only: in the app's Setup, allow "All files access" and
   "Unrestricted" battery. Syncthing-Fork must be installed.
4. Server with no screen: `sudo loginctl enable-linger <user>` so the
   collector runs without a sign-in, or run `monitor-agent` from any service manager.

## What runs, and what it costs

| Piece | Where | When | Budget (self-measured, logged) |
|---|---|---|---|
| `monitor-agent` | Windows, Linux | starts at sign-in, one cycle a minute, sleeps in between | under 40 MB, under 300 ms CPU per cycle |
| Alarm collector | Android | system alarm every 60 s (screen on) or 5 min (screen off), then exits | under 5 s and 500 ms CPU per run |
| The app | everywhere | only while open; reads the folder every 10 s, nothing in the background | under 150 MB |

Breaking a budget writes `OVER BUDGET` into that device's `log.txt`, which
syncs like everything else.

## Updates

Setup and updates > Install. The app downloads the release file, checks it
against the release's SHA256SUMS and hands it to the system installer.
Nothing installs without that tap.

## Layout

* `core/`: Dart package: file format, status reasoning (`status.dart`, tested),
  the computer collector (`bin/monitor_agent.dart`), update check.
* `app/`: Flutter app for Android, Windows and Linux. The Android collector is
  `app/android/app/src/main/kotlin/app/monitor/lite/Collector.kt`.
* `packaging/`: Windows installer (Inno Setup), Linux service and desktop files.
* `FORMAT.md`: the files in the shared folder.

Builds and releases: `.github/workflows/lite.yml` (Actions > Monitor Lite > Run
with a version publishes `lite-v<version>`).

## Replacing the old Monitor App

Version 2.0.0 and later replace the old .NET app in place:

* **Android:** same app id (`app.monitor.selfhosted`) and signing key, higher
  version, so the APK installs as an update. On first start it deletes
  everything the old app kept in its private storage.
* **Windows:** the installer finds the old app (its MSI, the `MonitorApp`
  service, the `MonitorAppUpdate` task, `Program Files\MonitorApp`,
  `ProgramData\MonitorApp`, `LocalAppData\MonitorApp`) and removes it, after one
  Windows "Yes" prompt. The log is `%TEMP%\monitor-app-remove-old.log`.
* **Fedora/Ubuntu:** the package is named `monitor-app` with a higher version,
  so it upgrades over the old package, then stops the old services and removes
  `/var/lib/monitor-app`, `/etc/monitor-app` and the `monitor-app` user.
