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
