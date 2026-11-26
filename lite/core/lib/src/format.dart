/// File names inside the shared folder. Kept in one place so the Android
/// collector (Kotlin) and this code agree; see lite/FORMAT.md.
const devicesDirName = 'devices';
const nowFileName = 'now.json';
const logFileName = 'log.txt';

/// Log file is cut back to its newest half when it passes this size.
const logMaxBytes = 256 * 1024;

/// While someone has the app open, it keeps a small file in watch/ saying so.
/// Collectors that see a fresh one collect every [watchIntervalSeconds]
/// instead of their resting rate, and go back by themselves when it expires.
const watchDirName = 'watch';
const watchIntervalSeconds = 10;
const watchHoldSeconds = 90;

/// Attached things live in their host's folder: devices/<host>/things/<id>/.
const thingsDirName = 'things';

/// Settings a thing's own software exported, read as they are.
const paramsFileName = 'params.json';
