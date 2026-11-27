# How Claude and ChatGPT work on Monitor App 2.x

Monitor App 2.x lives in `lite/`. It replaced the old .NET app; nothing
outside `lite/` is built for release any more. Two agents work on it for the
owner, Wilson:

- **ChatGPT**, branches `chatgpt/*`: owns **all UI**.
- **Claude** (Claude Code), branches `claude/*`: owns **everything behind
  the UI**.

`AGENTS.md` still applies. This file adds who owns what and the contract
between the two halves.

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
