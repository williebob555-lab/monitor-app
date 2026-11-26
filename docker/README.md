# Monitor App in Docker (Linux test servers)

This runs the Monitor App **peer node** (the always-on background service, the same program the `.deb`/`.rpm`
install) in a container. Use it on the Ubuntu server laptop and on the Fedora/Ubuntu test desktop instead of
installing the package. It has no window: you look at it from your phone, tablet or laptop once it is paired,
or through its admin page.

The image is built from the published release (`monitorapp-node-ubuntu-x64-<version>.deb` from the
`preview-<version>` GitHub release). The build checks the file against the release's `SHA256SUMS` and stops if it
does not match.

## What you need on the machine

- Docker with the compose plugin (`docker compose version` prints a version).
- Tailscale installed and logged in (`tailscale ip -4` prints a `100.x.y.z` address).

## First start (copy and paste, one machine at a time)

```bash
git clone https://github.com/williebob555-lab/monitor-app.git ~/monitor-app
cd ~/monitor-app/docker/node

cp .env.example .env
sed -i "s/^MONITOR_APP_PEER_BIND=.*/MONITOR_APP_PEER_BIND=$(tailscale ip -4)/" .env
cat .env                                  # check the address line shows your 100.x.y.z address

mkdir -p data && sudo chown 10055:10055 data && sudo chmod 700 data
docker compose up -d --build
docker compose ps                         # STATUS should say "Up ... (healthy)" within about a minute
```

If `docker` needs `sudo` on that machine, put `sudo` in front of each `docker` command.

## Pair it

The admin page only answers on the machine itself (`127.0.0.1:55432`), on purpose.

**On the Fedora desktop** (it has a screen): open `http://127.0.0.1:55432/` in its browser.

**On the headless Ubuntu server laptop**: from your laptop, open a tunnel, then open the page on your laptop.
Port 55442 is used on your side because your laptop's own Monitor App already uses 55432.

```bash
ssh -L 55442:127.0.0.1:55432 <your-user>@<server's tailscale address>
# leave that window open, then browse to http://127.0.0.1:55442/
```

On the admin page:

1. Under the phone section, press **Show a new pairing code**.
2. On the phone or tablet, add a device and scan the code (or choose enter code and type the address and code
   shown under the picture).
3. Back on the admin page, the phone appears under **Pending pairings**. Check its name and fingerprint, then
   approve it.

To pair your laptop with it directly, use **Invite a fresh peer** on the admin page with the laptop's peer ID,
fingerprint and Tailscale address, then approve the request on the laptop.

## Everyday commands (run in `~/monitor-app/docker/node`)

| What | Command |
| --- | --- |
| Is it running? | `docker compose ps` |
| Live log | `docker compose logs -f --tail 100` |
| Monitor App's own diagnostics log | `cat data/logs/diagnostics-$(date -u +%Y%m%d).log` |
| Stop / start | `docker compose stop` / `docker compose start` |
| Move to a new release | edit `MONITOR_APP_VERSION` in `.env`, then `docker compose up -d --build` |
| Remove the container (keeps its identity and pairings in `data/`) | `docker compose down` |

Do not delete `data/`: it holds this node's identity key, its pairings and its readings. Deleting it makes the
node a new, unpaired device.

## Sending logs for diagnosis

Everything the node logs is in two places: `docker compose logs` and `data/logs/diagnostics-*.log` (one file
per day, kept 7 days). To hand them over in one file:

```bash
docker compose logs --no-color --timestamps > monitor-app-docker.log 2>&1
tar czf monitor-app-logs-$(hostname).tgz monitor-app-docker.log data/logs
```

## What runs and what it can see

- **Host networking**: the admin page stays on the host's loopback, and the peer listener binds only the
  Tailscale address in `.env`. No ports are published. The node refuses to start if that address is not a
  private, Tailscale or link-local address.
- **Host process list** (`pid: host`): lets it report which services run (Tailscale, Syncthing, Docker...) and
  for how long. It runs as an unprivileged user (uid 10055) with every capability dropped, a read-only root
  filesystem and `no-new-privileges`, so it can see those processes but cannot signal or change them.
- **Tailscale status** (`compose.tailscale.yaml`): the host's `tailscale` program and `tailscaled` socket are
  shared read-only, so it reports connection state, its Tailscale address and which of your devices are online.
  Because the node is not root or a Tailscale operator, tailscaled refuses any change it might ask for.
  If you remove that file from `COMPOSE_FILE` in `.env`, it reports only whether Tailscale is running.
- **Readings that differ from a normal install**: CPU, memory, network, drives, and the host's OS name are the
  host's. Free/total storage is the filesystem holding Docker's data (usually the root filesystem). Syncthing
  shows running and uptime only; its folder details need Syncthing's API key, which the container does not get.
- **Updates**: the in-app updater is switched off inside the container (it reports "Update agent disabled").
  Updating means changing `MONITOR_APP_VERSION` and rebuilding.

## If something goes wrong

| You see | Meaning and fix |
| --- | --- |
| `required variable MONITOR_APP_PEER_BIND is missing a value` | `.env` is missing or has no address. Repeat the `cp` and `sed` lines above. |
| `bind source path does not exist: /usr/bin/tailscale` or `.../tailscaled.sock` | Tailscale is not installed there, or lives elsewhere. Install it, or delete `:compose.tailscale.yaml` from the `COMPOSE_FILE` line. |
| `Restarting` in `docker compose ps` | Look at `docker compose logs --tail 50`. "must bind to an explicit private, Tailscale, or local-link address" means the address in `.env` is wrong. "SQLite Error 14: unable to open database file" means the `chown` step was skipped: run the `mkdir`/`chown`/`chmod` line again. |
| `invalid configuration: --peer-bind must be an IP address` in the log | `.env` still says `100.x.y.z`. Run the `sed` line above. |
| `sha256sum: WARNING: 1 computed checksum did NOT match` during build | The download is not the published file. Do not use it; try again later. |
