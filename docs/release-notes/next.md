<!-- Printed at the top of the next preview's notes by eng/release/preview-notes.sh. Clear it after a release. -->
**The app tells you when Tailscale or Syncthing needs you, and can fix the common case.**

- **Alerts in plain words**: "Tailscale is not connected" after 5 minutes off your tailnet; "Syncthing folder has errors" after 5 minutes; "Syncthing folders not in sync" when a folder has been behind for 2 hours. Each says what it means and what to do, not a percentage.
- **Rescan Syncthing, one tap**: open a Syncthing alert in Alerts and press **Rescan Syncthing**. It asks Syncthing on that computer to look at every folder again (no files are changed), and says what happened. It only works from the computer the alert is about; anywhere else it tells you where to press it.
- **Renamed devices keep their name on the Map**: rename a found device in Things ("Living room router") and the Map uses that name too.
- You can switch any of the new alerts off under Alerts on this device.
