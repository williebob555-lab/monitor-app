/// File names inside the shared folder. Kept in one place so the Android
/// collector (Kotlin) and this code agree; see lite/FORMAT.md.
const devicesDirName = 'devices';
const nowFileName = 'now.json';
const logFileName = 'log.txt';

/// Log file is cut back to its newest half when it passes this size.
const logMaxBytes = 256 * 1024;
