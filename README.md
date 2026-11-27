# Monitor App

Monitor App 2.x shows the health of all your devices (phones, tablets,
Windows and Linux computers, servers) and the things attached to them (UPS,
audio interface, disks, containers). It has no server and no database:
each device writes small files into one Syncthing-shared folder, and Tailscale
answers "is it alive?".

Everything lives in [`lite/`](lite/):

* [lite/README.md](lite/README.md): what it does, how to install it on each device
* [lite/FORMAT.md](lite/FORMAT.md): the files every device writes (the data contract)
* [lite/SECURITY.md](lite/SECURITY.md): who may ask for what
* [lite/packaging/docker/README.md](lite/packaging/docker/README.md): the collector on a Linux server

Planning and collaboration: [docs/ROADMAP.md](docs/ROADMAP.md),
[docs/COLLABORATION.md](docs/COLLABORATION.md), [docs/VISION.md](docs/VISION.md),
[docs/SCENARIOS.md](docs/SCENARIOS.md), [docs/COMPANION.md](docs/COMPANION.md).

Releases: tags `lite-v<version>` on the GitHub releases page, built by
`.github/workflows/lite.yml`.

## The old .NET app

Monitor App 1.x (a .NET peer node with gRPC pairing and SQLite) was replaced
by 2.x in November 2026 and its code was removed from this repository on
2026-11-27 (roadmap row 36). It is still in the git history (the last commit
with it is the parent of the commit that removed it). Some documents in
`docs/` (ARCHITECTURE, DEVELOPMENT, TESTING, SECURITY, RELEASES, DECISIONS,
PLANNING, BILL-OF-WORK, CHANGELOG) and `TODO.md` describe that old app and are
kept as history only.
