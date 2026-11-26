#!/bin/sh
# Only on full removal, not on upgrade.
case "$1" in
  0|remove|purge) systemctl --global disable monitor-agent.service >/dev/null 2>&1 || true ;;
esac
exit 0
