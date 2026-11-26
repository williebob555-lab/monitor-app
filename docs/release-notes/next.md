<!-- Printed at the top of the next preview's notes by eng/release/preview-notes.sh. Clear it after a release. -->

**Screens stop freezing on phones and tablets.** Every screen refresh and every alert check used to sort all stored readings to find the newest value of each one: about 1.6 s per read at 500k readings on a fast server, and up to 27 s on the Pixel. The app now keeps the newest value of each reading up to date as readings arrive. The same read takes under 2 ms at any size. The first start after updating builds that list once, which takes a few seconds on a phone holding a lot of history.

**Phones stop working in the dark.** With the screen off, or with another app in front, Monitor App kept refreshing whatever screen was open, as often as once a second. It also kept asking your computers for fast one-second readings. Live screens now pause the moment Android hides the app and pick up again when you come back. Monitoring itself keeps running in the background as before.
