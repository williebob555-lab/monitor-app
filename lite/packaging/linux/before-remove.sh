#!/bin/sh
# Only on full removal, not on upgrade.
case "$1" in
  0|remove|purge)
    systemctl --global disable monitor-agent.service >/dev/null 2>&1 || true
    rm -f /etc/udev/rules.d/70-monitor-ups.rules
    systemctl disable --now monitor-lite-update.timer >/dev/null 2>&1 || true
    ;;
esac
exit 0
