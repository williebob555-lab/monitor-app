#!/bin/sh
# Replace the old .NET Monitor App completely: stop its services and remove
# what its package left behind (state, config, service user).
for u in monitor-app.service monitor-app-update.path monitor-app-update.service; do
  systemctl disable --now "$u" >/dev/null 2>&1 || true
done
rm -rf /var/lib/monitor-app /etc/monitor-app /opt/monitor-app /usr/libexec/monitor-app
userdel monitor-app >/dev/null 2>&1 || true
groupdel monitor-app >/dev/null 2>&1 || true
systemctl daemon-reload >/dev/null 2>&1 || true
# Start the collector for every user at sign-in. Servers without a sign-in
# session also need: sudo loginctl enable-linger <user>
systemctl --global enable monitor-agent.service >/dev/null 2>&1 || true
# Let the collector read a USB UPS directly (read-only status, no vendor
# software): its HID device becomes readable and gets a fixed name.
cat > /etc/udev/rules.d/70-monitor-ups.rules <<'RULES'
SUBSYSTEM=="hidraw", ATTRS{idVendor}=="0764", MODE="0644", SYMLINK+="monitor-ups"
SUBSYSTEM=="hidraw", ATTRS{idVendor}=="051d", MODE="0644", SYMLINK+="monitor-ups"
SUBSYSTEM=="hidraw", ATTRS{idVendor}=="0463", MODE="0644", SYMLINK+="monitor-ups"
SUBSYSTEM=="hidraw", ATTRS{idVendor}=="09ae", MODE="0644", SYMLINK+="monitor-ups"
RULES
udevadm control --reload >/dev/null 2>&1 || true
udevadm trigger --subsystem-match=hidraw >/dev/null 2>&1 || true
exit 0
