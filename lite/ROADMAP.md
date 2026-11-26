# Monitor App 2.x: the list

Worked top to bottom, in small releases. Each line says what you will see.
Sizes: **S** under an hour, **M** one to three hours, **L** a night or more.

## Done

| Release | What you see |
|---|---|
| 2.0.1 | One app on every device, replacing the old one. Each device writes its own files into the shared Syncthing folder, including while the app is closed. |
| 2.1.0 | **Live while you look**: with the app open anywhere, computers update every 10 s and phones every 15 s (screen on or charging). Servers can run the collector in Docker. |
| 2.2.0 | **Dive + map**: a map of the network (who reaches whom, ping times). Each device opens up into its parts, and each part opens into live and history charts. Home lists what needs a look. Computers say what they are (maker, model, processor), their CPU temperature (Linux), and how Tailscale (Linux) and Syncthing are doing. Phones report battery health, charge cycles and Wi-Fi signal. Computers with more than one drive list each. |
| 2.3.0 | **Phones' and tablets' latest readings now reach the other devices every time** (before, an update was skipped whenever the file kept the same size). Docker containers on servers (running, stopped, unhealthy) as a Containers part. Android memory line set to what a phone screen really costs. **Timeline under the map**: drag back up to 24 h to see who reached whom then. |

## Next

| # | Item | Size | What you will see |
|---|---|---|---|
| 1 | Verify 2.2.0 on your devices from their logs, and fix what shows up | M | Every device's parts filled in on every other device |
| 4 | Notifications, opt-in per device: "LAPTOP stopped reporting 30 min ago", "Syncthing folder stuck" | M | A switch per device; a phone notification only for the ones you switch on |
| 6 | Patterns instead of old raw data ("battery drains 4%/h with screen off") | L | A Patterns section on each part |
| 7 | Things beyond computers (IoT): one folder per thing next to `devices/` | L | Things on the map next to your devices |
