# Bill of work

Plain-English plan for Monitor App: where it stands, where it is going, and the work in between, in order. Each item says how big it is and what you will see when it lands. Kept current by whichever agent is working; the task queue in `docs/COLLABORATION.md` points here.

Sizes are agent working time: **S** is under an hour, **M** is one to three hours, **L** is a night or more.

## Where it stands (2026-11-23)

What you can use today:

- **Monitoring.** Your computers and phone run Monitor App. They pair with each other, collect readings, sync history, and show them on the Ecosystem and Dive screens.
- **Alerts.** These now fire only on real failures. Before 0.1.13, every reading a device lacked made its own alert.
- **Installers.** Windows .msi, Fedora .rpm, Ubuntu .deb and the Android .apk are all signed.
- **Updates.** Computers check every hour and have a Check for updates button. From 0.1.13 the phone updates itself too. Every install waits for your approval.

Why the last cycle felt like nothing changed: almost all of it went into work you can't see. That covered installers, signing, the update path, and the shared system model, which is the foundation for everything in `docs/VISION.md`. From 0.1.15 the model runs in the app and the new **Things** screen is built on it.

## End goal

The vision in `docs/VISION.md` and `docs/SCENARIOS.md`: one workspace to build, understand, organize and run your systems, physical and digital. Everything is one model, seen through many lenses (system, physical, inventory, history). It handles partial and unknown information, and always shows whether a value is measured, from a spec, entered, estimated or unknown.

## The work, in order

Done items stay listed so progress is visible.

| # | Item | Size | What you will see | Status |
|---|------|------|-------------------|--------|
| 1 | Updates that work: hourly checks, Check for updates button, last checked time, and the phone updating itself | M | Settings > Updates on every device. New versions arrive on their own and install when you press Install. | Done, 0.1.13 |
| 2 | Alert noise fix, plus an admin page that leads with Updates, Add a phone and pairings | S | A short alert summary instead of 100 rows | Done, 0.1.13 |
| 3 | **Phone pairing inside the app**: the QR code drawn in the desktop app, plus a "Let my phone reach this computer" switch that picks the Tailscale address. No PowerShell. | M | Settings > Pairings shows the QR; the phone scans it; you approve | Done, 0.1.14 |
| 4 | **Model in the running app** (T065): each computer builds its model from what it monitors (itself, its parts, paired devices, observed network devices) | M | Groundwork for 5; nothing new on screen by itself | Done, 0.1.15 |
| 5 | **Things**: the first screen on the shared model. Every object the app knows (computers, phone, parts, network devices, anything you add) as cards, each value tagged measured, spec, entered, estimated or unknown | L | A new "Things" area in the app | Done, 0.1.15 (computers and phone) |
| 6 | **Quick capture**: add a thing with just a name or a note, fill in details later, link it to other things, no forced folders | M | An "Add" button that takes a few words | Adding done, 0.1.15. Editing and linking next (T074) |
| 7 | **Open the machine**: an exploded view of a computer from the model (board, processor, memory, drives, network, battery), each part with its live reading | L | Tap a computer and it opens into its parts | Done, 0.1.16 |
| 8 | Lens shell (T070): one selection shared by every view, so switching lens keeps the thing you picked | M | Select a thing and switch between system, physical and inventory without losing it | Done, 0.1.17 (Things and the live view open each other; more lenses join as they arrive) |
| 9 | Connection engine (T064, T066, T067): ports, cables and adapters, with chain checks such as USB-C alt mode, audio level mismatch and DC polarity | L | Explanations of why a connection will or won't work, with evidence levels | Queued (ChatGPT's queue) |
| 10 | "What's missing" (T069): unconnected inputs, missing cables, power shortfalls | M | A list of what a build still needs | Queued |
| 11 | Physical layouts: racks, pedalboards and desks that look real, with drag to place | L | A rack or pedalboard view of real equipment | Queued |
| 12 | Time travel over the model: the as-of slider moves things and connections, not just readings | M | Drag back in time and see what was connected | Queued |
| 13 | Projects, parts lists, sourcing and prices (phase 3) | L | Projects with a bill of materials and where to buy | Later |
| 14 | Housekeeping: retire old controller code (T060), verify the MSI on your PC (T071) | M | Smaller downloads; nothing else | Background |

## How progress is reported

- Each finished item ships in a signed release. You get a short note saying what you'll see.
- This table is updated in the same change that finishes an item.
