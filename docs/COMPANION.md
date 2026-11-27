# Host telemetry companion (design, not built)

Status: pattern approved by Wilson 2026-11-27 ("the file-based UPower exporter
architecture is approved ... a promising architectural pattern for future
Docker, SMART and GPU telemetry providers"). Roadmap row 38. Only the UPower
exporter (`lite/packaging/linux/upower-export.sh`) runs today; every new
provider follows its contract below.

## The problem

On the servers the collector runs in Docker on purpose: no host D-Bus, no
`/dev` devices, no capabilities, SELinux enforcing, Wilson's user not in the
docker group. Some host data is out of reach from there:

| Data | What it needs on the host | Reachable from the container today? |
|---|---|---|
| UPS through UPower | the system D-Bus (any user may ask) | no; solved by the UPower export (2.6.5, 2.6.8) |
| UPS through USB HID | `/dev/hidrawN` read access (one udev rule) | yes, if the device is passed in (wc-alpha does) |
| SMART (disk health, wear, temperature, errors) | root or CAP_SYS_RAWIO / CAP_SYS_ADMIN on `/dev/sdX`, `/dev/nvmeN` | no |
| GPU, AMD (the 5600G's iGPU) | `/sys/class/drm/card*/device/gpu_busy_percent`, hwmon | yes: Docker shows the host's sysfs read-only |
| GPU, NVIDIA | `nvidia-smi` and its device nodes | no (none in the fleet today) |
| Docker containers | the Docker socket (root-equivalent) | yes, through the GET-only `docker-list` proxy |

## Options

1. **Give the container more** (D-Bus socket, disk devices, capabilities).
   Rejected: each one widens what a compromised collector could do, and on
   Fedora it needs SELinux relabels or `privileged`. It also mixes monitoring
   with access that shutdown-critical services use.
2. **Run the collector natively** (the .rpm/.deb) on the servers. UPower then
   works with no export (2.6.8 asks `upower -d` itself). SMART still needs
   root, and the Docker socket would need the docker group (root-equivalent),
   so it doesn't remove the problem, and it gives up the Docker deployment
   Wilson chose.
3. **One small host companion that writes files** (recommended). The same
   idea as the UPower exporter, generalised: a host script with one systemd
   timer per source, each source running with the least it needs and writing
   one small file into one export folder that the container mounts
   read-only. The collector keeps no new privilege. "Everything is a file"
   holds on the host too.

## Recommended design

* `packaging/linux/host-export.sh` (the UPower exporter grows into it;
  `upower-export.sh` stays as a shim so existing timers keep working).
* `--check SOURCE` shows what it would export and changes nothing;
  `--install SOURCE --dir DIR --user USER` adds that source's timer;
  `--remove SOURCE` removes it. Sources are opt-in one by one.
* **The provider contract** (what the UPower exporter already does, and every
  provider must):
  * Read-only towards the source: never writes to a device, a service or its
    configuration; never part of shutdown or power policy.
  * Least privilege per provider: its own systemd unit, the smallest user and
    capability set that source needs, `ProtectSystem=strict`, only its export
    folder writable, no network.
  * Atomic writes: temp file in the same folder, renamed over the export.
  * `exported: <unix time>` first; `error: <reason>` when the source didn't
    answer, so the collector shows unknown with that reason instead of old
    numbers; the collector also treats a file older than the source's limit
    as unknown.
  * C locale for every tool it runs; raw units where the tool offers them.
  * Configuration resolved the way the source does (UPower: main file, then
    drop-ins by name, last wins), with the files named.
  * `RequiresMountsFor=` its folder and never creates it at export time; the
    container mount uses `create_host_path: false` and `selinux: z`, and the
    folder sits outside anything the container can write.
  * Bounded size (the collector ignores an export over 64 KB) and a `--check`
    that changes nothing.
* Sources:
  * `upower`: as today. User, no capabilities, Unix sockets only, every
    minute.
  * `smart`: `smartctl -j -x -n standby` for each disk, reduced to the fields
    shown (model, serial, health verdict, temperature, power-on hours, media
    and wear, error counts). The only root unit: `CapabilityBoundingSet=
    CAP_SYS_RAWIO CAP_SYS_ADMIN`, `DeviceAllow=` block devices only,
    `ProtectSystem=strict`, `ReadWritePaths=` the export folder, no network.
    Every 30 minutes, and never wakes a sleeping disk (`-n standby`). Needs
    `smartmontools` on the host.
  * `gpu`: only where a GPU's data isn't in sysfs (NVIDIA:
    `nvidia-smi --query-gpu=... --format=csv`). The AMD iGPU is read by the
    collector straight from sysfs, no companion needed.
  * Docker stays on the GET-only proxy; a companion export would be no safer.
* Disks and GPUs become things (`disk`, `gpu` node types) with the same id
  rule as USB things: model plus serial, so both systems of a dual-boot
  machine write the same disk.
* Shutdown and power policy are never touched: no source writes to a device
  or a service.

## Size and gate

M (about two sittings): the script and units, the SMART reader and things in
core, tests from captured `smartctl -j` output, then verification on
wc-alpha / wc-desktop. It adds a root timer on Wilson's hosts, so it waits for
his OK (ROADMAP section 5). GPU (NVIDIA) only when such a GPU exists.
