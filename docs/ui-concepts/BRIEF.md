# Interface concept prototype brief (shared by all concept builders)

## Why
Wilson (owner) saw three rounds of previews and said each was "the exact same interface". Every one so far used the
same structure: left rail + central map canvas + right inspector panel + bottom timeline; only the skin changed.
He now needs to compare STRUCTURALLY DIFFERENT interfaces: different primary surface, different way of navigating,
different way details and actions appear. Do NOT build a rail + canvas + inspector + timeline layout. If your page
could be described as "a map with a side panel", start over.

## The product
Monitor App: a home-network/systems monitor. Every device (Windows laptop, Fedora box, Ubuntu home server, Android
phone and tablet) runs a peer; they replicate observations in real time. The phone is a mobile command center and
must be as usable as desktop. Security matters: risky actions need a fingerprint (show a fingerprint prompt moment).
Monitoring controls available: pause/resume collection, change sampling rate, acknowledge/mute alerts, approve a
new device pairing (fingerprint), approve an update install (fingerprint). Time travel: you can look at the state
at any past moment ("as of 21:14"), clearly marked as recorded, not live. Stale vs offline are different states.

## Shared sample environment (use exactly this so concepts are comparable)
Devices: Home router (gateway 192.168.1.1), Home server (Ubuntu 24.04; hosts Syncthing ok, Media server degraded,
Backup FAILED 3 nights: target disk 94% full), Laptop (Windows 11, Wi-Fi 5 GHz -58 dBm, syncing Photos -> server
2.1 GB, 40%, 112 Mb/s), Fedora box (Fedora 41, wired, went OFFLINE at 21:14), Tablet (Android 14, STALE: last seen
22:31), Phone (Android 15, away on LTE via Tailscale relayed 180 ms, charging 64%), NAS (PLANNED, 4-bay, not bought).
Server internals: CPU 23%, RAM 11.2/32 GB, 2 drives (NVMe system 41%, HDD data 94% <- why backup fails), temps 52C.
Pending: a new device "Pixel-Watch" asks to pair (needs fingerprint approval). Update 0.4.2 downloaded+verified,
waiting for approval on Laptop and Server.
Events tonight: 21:14 Fedora box offline; 22:31 Tablet stops reporting; 02:00 Backup failed (3rd night);
23:05 Photos sync started; 23:20 Update 0.4.2 verified.
Current time 23:23.

## Visual language (from Wilson's confirmed spec, keep)
Dark graphite base (#0f1114 / #15181c panels), restrained. Semantic colors: green #3ecf8e ok, cyan #38b6e8 transfer,
amber #f0b429 warn/stale, red #ef5350 fault, violet #a78bfa planned, gray #6b7280 offline/inactive. Status shown by
localized indicators (dot, ring, stripe, badge + words), not by painting whole objects. Health always shape + words.
Fonts via Google Fonts: "IBM Plex Sans" and "IBM Plex Mono" (or Inter + JetBrains Mono). Must feel ALIVE: streaming
numbers, moving transfer indicators, 1 s ticks. Quality bar: a polished product, not a wireframe.

## Deliverable
- One self-contained file: concepts/<your-id>/index.html (inline CSS+JS; only Google Fonts external; canvas/SVG ok).
- Responsive: must work and look designed at desktop 1400x860 AND phone 390x844 (the phone version is first-class,
  touch-sized, not a squished desktop). Use a 16px side gutter on phone, no horizontal scroll.
- Interactive enough to feel the concept: navigation, opening a device, one fingerprint-approved action (simulated
  prompt), time travel to 21:14 and back, seeing the failing backup root cause.
- Give `body` an explicit background; define colors as CSS custom properties on :root.
- Verify with Playwright (require('playwright'); chromium.launch() needs no options): screenshot desktop and phone at
  deviceScaleFactor 1 to concepts/<id>/desk.png and phone.png, look at them, fix layout problems (overlaps, clipped
  text, empty areas). Also screenshot one "opened detail" state. Check console for errors.
- Do not publish anything; do not touch the git repo. Report: the concept in 3 sentences, what interactions exist,
  and the screenshot paths.

## Budget
Usage is nearly exhausted. Aim for one compact, polished page (roughly 500-800 lines). At most two screenshot/fix
rounds. Do not explore the repo or other files beyond this brief.
