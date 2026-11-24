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
| 11 | Physical layouts: racks, pedalboards and desks that look real, with drag to place | L | A rack or pedalboard view of real equipment | Racks done, 0.1.22 (drawn on the card, place at a unit, fit checks). Pedalboards and desks done (drawn from above to scale, place in mm, overhang and overlap checks). Drag to place done. Item 11 done |
| 12 | Time travel over the model: the as-of slider moves things and connections, not just readings | M | Drag back in time and see what was connected | Things follow the timeline, 0.1.18 (which things existed, what was inside what, their values and readings then; editing off in the past). Connections follow once they are drawn |
| 13 | Projects, parts lists, sourcing and prices (phase 3) | L | Projects with a bill of materials and where to buy | Planned 2026-11-24, see "Projects, parts and sourcing" below. P1 and P2 done |
| 14 | Housekeeping: retire old controller code (T060), verify the MSI on your PC (T071) | M | Smaller downloads; nothing else | Background |

## Make the app useful on its own (planned 2026-11-24, mostly done)

**Your feedback (2026-11-24).** "The app really doesn't do much right now." Tailscale, Syncthing and the other things you run are not monitored, even though the vision promises it.

**You are right.** Here is what each computer finds out by itself today: processor load, memory, disk space, network traffic, its own network adapters and links, and the phone's battery. Everything else (Things, projects, racks, values) only knows what you type in. No code reads services at all. Syncthing appears only in the vision documents and the sample scene, and Tailscale is used only for pairing addresses.

**End goal.** You install the app and each computer fills itself in: what it is (maker, model, processor, memory, drives, operating system), what runs on it (services and apps you rely on, running or stopped), and how those are doing, all without typing. Problems raise alerts. Your laptop, phone and tablet see all of it, reliably.

**Order (2026-11-24).** First pairing that just works (R1 to R6 above). Then readings flowing reliably between laptop, phone and tablet (R4 and R5). Then the items below. Projects (P3 onwards) and other new features wait.

| # | Item | Size | What you will see | Status |
|---|------|------|-------------------|--------|
| D1 | **Services on each computer**: read the running services (systemd on Fedora and Ubuntu, Windows services) and recognise well-known ones such as Tailscale, Syncthing, SSH, Docker and web servers. Each one becomes a thing on its computer, marked measured, with running or stopped and since when | M | A "Services" group on each computer, filled in by itself | Done, next release (see "Fill the model by itself" D5) |
| D2 | **Tailscale, read properly**: connected or not, this computer's Tailscale address, which of your devices are online, exit node. Read-only, using `tailscale status --json` | M | "Tailscale: connected, 4 of 5 devices online" on the computer | Done, next release (see D3 and D5 below) |
| D3 | **Syncthing, read properly**: running or not, each folder (up to date, syncing, error, out of sync) and which devices are connected. Read-only, using Syncthing's own local API on 127.0.0.1 with the key from its config file | M | "Syncthing: 3 folders up to date, Laptop and Phone connected" | Done, next release (key handed over by the desktop app) |
| D4 | **Alerts for them**: service stopped, Tailscale disconnected, Syncthing folder stuck or in error, each with how long and why | S | An alert when Syncthing stops syncing | In progress (Tailscale disconnected, Syncthing folder in error or behind) |
| D5 | **What the machine is, found by itself**: maker, model, serial, processor, memory size, drives, operating system and version, as measured values on the computer's card. No typing | M | A computer's card says "Lenovo ThinkPad …, 16 GB, Fedora 41" by itself | Done, next release (see D4 below; serial number not read) |

## Fill the model by itself (started 2026-11-24)

**Goal.** In the morning the Map and Things show your real network, not just two devices: the router, the SG250 switch, printers, TVs, phones, every Tailscale device, and what each computer is made of. Everything found is marked as discovered or measured, can be renamed or confirmed, and is never trusted into the mesh by being found.

**Where it stands.** Each computer reports only its own CPU, memory, disks, network traffic and adapters, plus the phone's battery. Nothing looks at the rest of the network (the security rules forbid reading other devices' addresses), Tailscale and Syncthing are not read at all, and a computer does not know its own maker, model or graphics card.

**Your decision (2026-11-24):** look for other devices with a switch per computer ("Find devices on this network"), off until you turn it on, own subnet only, slow.

| # | Item | Size | What you will see | Status |
|---|------|------|-------------------|--------|
| D1 | **Find devices on this network** (switch per computer, off by default): reads the computer's neighbor table, a slow ping of its own subnet only (at most one /22, 16 at a time, every 30 minutes), local names (DNS, mDNS), UPnP/SSDP descriptions (TV, router, printer names and models) and the maker from the MAC prefix. Each device becomes its own subject with signed readings, so it replicates to every device in the mesh | L | Turn the switch on in Settings and, within a minute, dozens of devices appear on the Map around the computer that found them, with names, makers and "answered the last scan" | Done, next release |
| D2 | **Discovered devices become Things**: one Thing per device (same MAC on two computers gives one Thing), kind guessed from what it offers (printer, TV, router, phone), marked discovered/estimated; rename and confirm as usual | M | Things lists the router, printers, TVs and phones with their maker and address | Done, next release |
| D3 | **Tailscale devices**: `tailscale status --json` on each computer; every tailnet device becomes a device with its Tailscale address, OS and online state; whether this computer is connected | M | All tailnet devices on the Map; "Tailscale connected" on each computer | Done, next release |
| D4 | **Hardware self-description**: maker, model, processor, memory, drives, graphics, and installed systems (firmware boot entries on Linux: Fedora running now, Windows Boot Manager also installed; no warnings) as measured readings that fill in the computer's Thing | M | Open a computer in Things and see "Lenovo ... , Intel Core ..., 16 GB, Samsung SSD 1 TB, Windows and Fedora installed" | Done, next release (Windows lists only itself) |
| D5 | **Services**: Tailscale and Syncthing as services running on their computer (folders up to date, syncing or stuck; devices connected). Syncthing's key is read on the computer and never leaves it | M | Tailscale and Syncthing nodes on the Map, linked to their computer | Done, next release (Syncthing key handed over by the desktop app) |
| D6 | Actions and diagnosis (rescan a folder, reconnect, restart) on the same signed control path, with fingerprint or device PIN | L | One-tap fixes on a stuck folder | After D1-D5 |

## Projects, parts and sourcing (planned 2026-11-24)

**Goal.** A project is a thing like any other (kind "project"). Its parts list is what the model already knows: things linked "part of" it, or put inside it. The app tells you what you already own, what you still need to buy, what that costs, and where to buy it. Every price and quantity shows whether it was measured, from a listing, entered, estimated or unknown. No separate shopping screen and no code written for one kind of project.

**Where it stands.** You can already add a project, add a requirement, link things "part of" it, and enter a price as your own value. Nothing adds these up or shows them as a list yet. "What's missing" finds cables, adapters and power a build needs, but it is not tied to a project.

| # | Item | Size | What you will see | Status |
|---|------|------|-------------------|--------|
| P1 | **Parts list on a project card**: every part with how many are needed, its stage (idea, planned, ordered, owned, installed), and a "to buy" mark for anything you don't own yet | M | Open a project in Things and see its parts list, with "3 of 8 owned, 5 to buy" | Done, next release |
| P2 | **Cost**: price per part times quantity, a total for what's left to buy, and a note when a price is missing or only estimated. Different currencies are shown side by side, never converted silently | S | "To buy: about $214 (2 prices unknown)" on the project | Done, next release |
| P3 | **What's missing joins the project**: cables, adapters and power the parts need show up on the project's list, and "Add to my list" adds them as parts | M | The project list grows the cable you forgot | Planned |
| P4 | **Where to buy**: add a listing (shop, price, link, in stock) to a part; the cheapest known listing is used for the total, with the date the price was seen | M | Each part to buy shows its shops and prices, and how old each price is | Planned |
| P5 | **Order and arrive**: mark a part ordered, then owned when it arrives; the project's progress and the time-travel slider follow | S | Progress moves as parts arrive | Planned |
| P6 | **Price watch**: an alert when a listing you track drops below a price you set (checked only when you press Refresh or on a schedule you turn on; nothing fetched without your say) | M, uncertain | "The DI box is now $39 at …" in alerts | Later; needs a decision on which shops can be read |

P1 and P2 ship together as the first release, then P3, then P4 and P5.

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
| M3 | **Mesh screen** (T086): who is in the mesh, who added whom and when, last seen, and Remove (with fingerprint) | M | A clear list instead of per-device pairings | Done, next release (alert, who added whom, fingerprint on phone removal) |
| M4 | **Pass readings along** (T087): readings travel through any device that is online, still signed by the device that took them, so a relay cannot change them | L | The phone sees the tablet, and a laptop it can't reach directly, through any computer | Done, next release |
| M5 | **Phones accept connections** (T088): investigate running a listener on Android so two phones can sync with no computer on | L, uncertain | Phone and tablet sync with each other when no computer is on | Later; may not be possible on Android |

### Make pairing a complete system (planned 2026-11-24)

**Your feedback (2026-11-24).** Pairing feels like scraps, not a finished system. Remove did nothing on the laptop or the phone. The phone and laptop still don't share readings. A removed device must be able to pair again normally, without a reinstall. Silent stutters destroy trust.

**End goal.** Every pairing action answers on the spot: what it is doing, then whether it worked and, if not, why. Each device's card says whether it is connected right now and, when readings aren't arriving, the reason in plain words. Removing a device takes effect everywhere at once, and the device can come back later through a fresh pairing that you approve.

**What was wrong.**
- **Remove did nothing.** Remove did work, but its "Are you sure?" box appeared at the top of the device list, usually out of view, so pressing Remove looked like it did nothing.
- **Fingerprint.** Remove asks for a fingerprint only when one is set up. Nowhere tells you whether one is set up, or what to use instead.
- **Rejoining.** Removal is permanent by design (`MeshMembership`: a removal "wins over any admission"). A removed device can't rejoin without being reinstalled.
- **No reason shown.** When readings stop, nothing on screen says why.

| # | Item | Size | What you will see | Status |
|---|------|------|-------------------|--------|
| R1 | **Remove where you press it**: the question, a progress bar and the result (or the reason it failed) appear on that device's own card | S | Press Remove and the card asks, works, then says "Removed" or why not | Built, 0.1.28 |
| R2 | **Fingerprint said plainly**: every action that can use a fingerprint says whether one is set up. When none is, it uses your phone's PIN or your Windows Hello PIN instead and shows how to set a fingerprint up. Nothing fails silently. | M | "Fingerprint not set up: your PIN will be asked instead. Set up fingerprint…" | Built, 0.1.28 (removing and adding devices on phones) |
| R3 | **Pair again after removal**: a fresh pairing that you approve on a trusted device brings a removed device back, keeping its history. Removal still takes effect everywhere at once. The approval names exactly which removals it lifts, so a newer removal always wins, whatever the device clocks say. | L, security review | Remove the phone, scan the laptop's code again, approve, and it is back | Built, next release |
| R4 | **Connection state on each device**: connected, trying (and which address), or can't reach (with the error); when the last reading arrived; readings waiting to send | M | "Laptop: can't reach 100.x.y.z (timed out), last reading 2 h ago; trying 192.168.1.20" | Built, 0.1.28 |
| R5 | **Phone and laptop readings flowing**: find the cause with R4's details and fix it | M, uncertain until R4 shows the reason | Phone and laptop show each other's readings | Planned |
| R6 | **Every pairing button answers**: Approve, Reject, Show code, Add a phone or tablet, Save permissions each show busy, then success or the reason it failed, on the spot | S | No button press goes unanswered | Built, 0.1.28 |

Order: R1, R2, R6 and R4 ship together first (0.1.28). R3 next, then R5 as soon as R4 shows the cause. The services work (D1 to D5) moved to its own thread on 2026-11-24.

## How progress is reported

- Each finished item ships in a signed release. You get a short note saying what you'll see.
- This table is updated in the same change that finishes an item.
