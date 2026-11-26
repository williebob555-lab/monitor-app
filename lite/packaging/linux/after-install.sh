#!/bin/sh
# Start the collector for every user at sign-in. Servers without a sign-in
# session also need: sudo loginctl enable-linger <user>
systemctl --global enable monitor-agent.service >/dev/null 2>&1 || true
exit 0
