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
| 6 | **Quick capture**: add a thing with just a name or a note, fill in details later, link it to other things, no forced folders | M | An "Add" button that takes a few words | Adding done, 0.1.15. Rename, kind, note and putting a thing inside another done, 0.1.17. Stage, tags, your own values (entered or from a spec) and links (depends on, powered by, runs on, part of, replaces, about, connected to) done, 0.1.18 |
| 7 | **Open the machine**: an exploded view of a computer from the model (board, processor, memory, drives, network, battery), each part with its live reading | L | Tap a computer and it opens into its parts | Done, 0.1.16 |
| 8 | Lens shell (T070): one selection shared by every view, so switching lens keeps the thing you picked | M | Select a thing and switch between system, physical and inventory without losing it | Done, 0.1.17 (Things and the live view open each other; more lenses join as they arrive) |
| 8a | **The map is back**: the node-and-connection graph of the ecosystem as a "Map" switch beside the Dive; select a device to dive in or open it up in Things | S | A Map button in the Ecosystem toolbar | Done, 0.1.17 |
| 8b | **Menu lag fix** (high priority): phone reads off the screen thread, the shared model cached between changes, Things keeps unchanged cards | S | Menus respond at once again | Done, 0.1.17 |
| 8c | **Readings by category**: a computer's readings under System, Network, Power headings, big groups folded | S | Short cards instead of one long list | Done, 0.1.17 (Things) |
| 9 | Connection engine (T064, T066, T067): ports, cables and adapters, with chain checks such as USB-C alt mode, audio level mismatch and DC polarity | L | Explanations of why a connection will or won't work, with evidence levels | Done, 0.1.20 (ports, plugging in, chain checks and verdicts on each card) |
| 10 | "What's missing" (T069): unconnected inputs, missing cables, power shortfalls | M | A list of what a build still needs | Done, 0.1.20 ("What's missing" in Things, with Add to my list; cable lengths come with item 11) |
| 11 | Physical layouts: racks, pedalboards and desks that look real, with drag to place | L | A rack or pedalboard view of real equipment | Racks done, 0.1.22 (drawn on the card, place at a unit, fit checks). Pedalboards and desks done (drawn from above to scale, place in mm, overhang and overlap checks). Drag to place is next |
| 12 | Time travel over the model: the as-of slider moves things and connections, not just readings | M | Drag back in time and see what was connected | Things follow the timeline, 0.1.18 (which things existed, what was inside what, their values and readings then; editing off in the past). Connections follow once they are drawn |
| 13 | Projects, parts lists, sourcing and prices (phase 3) | L | Projects with a bill of materials and where to buy | Later |
| 14 | Housekeeping: retire old controller code (T060), verify the MSI on your PC (T071) | M | Smaller downloads; nothing else | Background |

## Mesh pairing (started 2026-11-23)

**Goal.** Your devices form one mesh. Add a new device from *any* device you already have (computer, laptop, tablet or phone, including phone to phone) and every other device trusts it. Remove it on any device and it is removed everywhere. Readings reach every device through whichever devices are online.

**Where it stands.** Today a phone or tablet can only pair with a computer, by scanning that computer's QR code. Four things in the code cause this:

1. The phone can only dial out. The server part the computers use to accept connections does not run on Android, so nothing can connect *to* a phone or tablet.
2. Trust is strictly pairwise. Every pair of devices is approved separately (`docs/SECURITY.md`: "transitive trust ... out of scope").
3. Readings are never passed along. A device only sends the readings it collected itself (`OriginReplicationCoordinator`: "relayed origin records are not supported").
4. Only computers show a pairing QR code.

**Your decision (2026-11-23):** add once, trusted everywhere. The device you add from asks for your fingerprint, every device alerts you when someone joins, and the right to *control* a device still stays off until you grant it on that device.

| # | Item | Size | What you will see | Status |
|---|------|------|-------------------|--------|
| M1 | **Shared member list** (T080): every device keeps a list of signed "joined" and "removed" notes and swaps it whenever two devices talk. Pairing anything to any device spreads to all of them, and so does removing one. Existing pairings are carried over automatically. | L | Pair the phone with one computer and it appears on, and connects to, every computer. Remove a device in one place and it disappears everywhere. | Done, 0.1.18 (fixes in 0.1.19) |
| M2 | **Add a device from a phone or tablet** (T085): "Add a device" on the phone scans the new device's code, asks for your fingerprint, then shows a code back. Works phone to phone, phone to tablet, tablet to phone. | M | Phone and tablet can bring each other (and new computers) into the mesh face to face | Done, 0.1.21 |
| M3 | **Mesh screen** (T086): who is in the mesh, who added whom and when, last seen, and Remove (with fingerprint) | M | A clear list instead of per-device pairings | Queued |
| M4 | **Pass readings along** (T087): readings travel through any device that is online, still signed by the device that took them, so a relay cannot change them | L | The phone sees the tablet, and a laptop it can't reach directly, through any computer | Done, next release |
| M5 | **Phones accept connections** (T088): investigate running a listener on Android so two phones can sync with no computer on | L, uncertain | Phone and tablet sync with each other when no computer is on | Later; may not be possible on Android |

## How progress is reported

- Each finished item ships in a signed release. You get a short note saying what you'll see.
- This table is updated in the same change that finishes an item.
