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

To see this server's containers in the app (running, stopped, unhealthy), uncomment
the two Docker-socket lines in `compose.yaml` and set `DOCKER_GID` in `.env`
(`getent group docker | cut -d: -f3`). The collector only reads the list, but
whoever holds the socket can control Docker, which is why it is off by default.
Installed without Docker (the .rpm/.deb), the collector reads it if your user is in the docker group.

## UPS (optional)

A USB UPS (CyberPower, APC, Eaton, Tripp Lite) is read directly over USB: on
mains or battery, charge, load and runtime. No vendor software and no NUT. One
rule lets your own group (the one the collector runs as, `GID` in `.env`)
read it, nobody else, and gives it a fixed name:

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
make). It never sends anything to the UPS.

An audio interface's readings need the `/proc/asound` line in `compose.yaml` uncommented.

## Update

```sh
cd ~/monitor-app && docker compose build --pull --no-cache && docker compose up -d
```

That fetches the newest release and checks its SHA256 before using it.
To pin a version, set `VERSION=2.0.1` in `.env` instead of `latest`.

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
