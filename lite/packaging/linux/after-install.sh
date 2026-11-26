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
exit 0
