# Monitor App on a Linux server (Docker)

Runs only the collector: it writes this server's battery, CPU, memory, disk,
network and what it can reach into the shared Monitor folder, once a minute
(every 10 s while someone has the app open). It has no window and listens on no port.

## First time

```sh
mkdir -p ~/monitor-app && cd ~/monitor-app
curl -fsSLO https://raw.githubusercontent.com/williebob555-lab/monitor-app/main/lite/packaging/docker/Dockerfile
curl -fsSLO https://raw.githubusercontent.com/williebob555-lab/monitor-app/main/lite/packaging/docker/compose.yaml
curl -fsSL  https://raw.githubusercontent.com/williebob555-lab/monitor-app/main/lite/packaging/docker/.env.example -o .env
nano .env          # set MONITOR_FOLDER; check UID/GID with: id -u ; id -g
mkdir -p data
docker compose up -d --build
```

Within a minute the server shows up in Monitor App on every device.

## Containers (optional)

To see this server's containers in the app (grouped by Compose project, with
state, health, CPU, memory and a link to each one's web page), uncomment the
`docker-list` service and the `DOCKER_HOST` line in `compose.yaml`, then
`docker compose up -d`. The collector never gets the Docker socket: the small
proxy holds it and only answers GET requests for the container list and
stats; anything that could change Docker is refused. Your user needs no
docker group. A container's link comes from a `monitor.url` label if it has
one (for host-network containers like Syncthing or Pi-hole), else from its
first published port, and only if that port answers.

## UPS (optional)

A USB UPS (CyberPower, APC, Eaton, Tripp Lite) can reach the collector two
ways, and it uses both when it has both. No vendor software and no NUT.

* **UPower** (preferred, see "UPS through UPower" below): the standard Linux
  power service most desktops already run, often the one that shuts the
  machine down at low battery. Gives on mains or battery, charge, runtime and
  what the host does when the battery runs low.
* **USB HID directly** (this section): adds the load and the beeper setting,
  which UPower doesn't report, and stands in when UPower isn't there.

With only UPower the load shows as unknown, with the reason. For the direct
path, one rule lets your own group (the one the collector runs as, `GID` in
`.env`) read it, nobody else, and gives it a fixed name:

```sh
sudo tee /etc/udev/rules.d/70-monitor-ups.rules >/dev/null <<RULES
SUBSYSTEM=="hidraw", ATTRS{idVendor}=="0764", MODE="0640", GROUP="$(id -gn)", SYMLINK+="monitor-ups"
SUBSYSTEM=="hidraw", ATTRS{idVendor}=="051d", MODE="0640", GROUP="$(id -gn)", SYMLINK+="monitor-ups"
SUBSYSTEM=="hidraw", ATTRS{idVendor}=="0463", MODE="0640", GROUP="$(id -gn)", SYMLINK+="monitor-ups"
SUBSYSTEM=="hidraw", ATTRS{idVendor}=="09ae", MODE="0640", GROUP="$(id -gn)", SYMLINK+="monitor-ups"
RULES
sudo udevadm control --reload && sudo udevadm trigger --subsystem-match=hidraw
ls -l /dev/monitor-ups /dev/$(readlink /dev/monitor-ups)   # crw-r----- root <your group>
```

Then uncomment the `devices:` line in `compose.yaml` and run `docker compose up -d`.
On a dual-boot machine, do this on each system. The .rpm/.deb installs its own rule.

The collector opens the device read-only and only asks for the UPS's status
reports (the kernel's get-feature-report call, which a read-only handle may
make). It never sends anything to the UPS on its own.

**The UPS's beeper.** The UPS page shows its alarm setting (on, off, or quiet
until the next event), and you can change it there from any device: the
collector applies it on its next check and logs who asked. Those requests
are the only thing that ever writes to the UPS. By hand, in this project's
folder:

```sh
sudo docker compose exec monitor monitor-agent --ups-alarm off    # or: on, quiet
```

The setting lives in the UPS itself. Turning it off also silences the
low-battery warning, so you rely on the app for that.

## UPS through UPower (recommended where the host runs UPower)

If UPower on the host already reads the UPS (for example because it shuts the
machine down at low battery), the collector shows what UPower sees without
getting the USB device or D-Bus. A small timer on the host writes what UPower
says about each UPS (never a laptop battery, mouse or keyboard), its own
version and low-battery action, and the policy lines of
`/etc/UPower/UPower.conf` into one file once a minute, as your user, and the
container gets only that folder, read-only. It only reads from UPower, never
writes to the UPS or to UPower, and has no part in shutdown: UPower's own
action stays exactly as configured.

```sh
curl -fsSL -o /tmp/upower-export.sh https://raw.githubusercontent.com/williebob555-lab/monitor-app/main/lite/packaging/linux/upower-export.sh
sh /tmp/upower-export.sh --check          # what UPower sees; changes nothing
sudo sh /tmp/upower-export.sh --install --dir /var/lib/monitor-app/upower --user "$(id -un)"
```

Then uncomment the `/host/upower` line in `compose.yaml` (same folder as
`--dir`) and run `docker compose up -d`. You get on mains or battery (and
"battery, low" when UPower warns), charge, runtime (the UPS's own estimate),
maker, model and "Host protection" (for example "host powers off at 20 %").
If the USB step above is also done, load and the beeper come from USB HID and
"Read through" says so. An export older than 3 minutes is shown as unknown,
never as current. Needs collector 2.6.8 or later for the host protection,
the HID supplement and several UPSes (2.6.5 reads the basics). Rerun
`--install` to update the timer's script. Remove with
`sudo sh /usr/local/lib/monitor-app/upower-export.sh --remove`.

Installed from the .rpm or .deb (no Docker), the collector asks UPower itself
(`upower -d`, as any user may), so none of this is needed.

An audio interface's readings need the `/proc/asound` line in `compose.yaml` uncommented.

## Update

Automatic (recommended), once per deployment. `--dir` is the folder holding
its `compose.yaml`; everything else (service, image, Monitor folder, state,
device id) is read from that project's Compose config:

```sh
curl -fsSL -o /tmp/update.sh https://raw.githubusercontent.com/williebob555-lab/monitor-app/main/lite/packaging/docker/update.sh
sudo sh /tmp/update.sh --dir ~/monitor-app --check     # shows what it found; changes nothing
sudo sh /tmp/update.sh --dir ~/monitor-app --install   # timer for this project only (run again to update the timer)
```

Every 15 minutes a root systemd timer for that one project checks for a
newer release and, if there is one, rebuilds and restarts only its collector
service (`--no-deps`; no other container is touched). The Dockerfile checks the
SHA256 first. Each attempt is written to that device's log in the app. Your
user needs no docker group. Update now: `sudo sh /usr/local/lib/monitor-app/update.sh --dir ~/monitor-app`.
Overrides if detection is wrong: `--service --image --folder --state --id --project`.

By hand instead:

```sh
cd ~/monitor-app && docker compose build --pull --no-cache && docker compose up -d
```

To pin a version, set `VERSION=2.0.1` in `.env` instead of `latest`; the timer
then leaves it alone.

## Check it

```sh
docker compose logs --tail 20          # only errors go here
tail ~/Infrastructure/Monitor/devices/*/log.txt   # the collector's own log
```

## Remove

```sh
cd ~/monitor-app && docker compose down
```

The device's folder stays in the Monitor folder; hide it in the app, or delete
`devices/<id>` once you're sure.
