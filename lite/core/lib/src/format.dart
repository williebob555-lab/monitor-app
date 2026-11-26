/// File names inside the shared folder. Kept in one place so the Android
/// collector (Kotlin) and this code agree; see lite/FORMAT.md.
const devicesDirName = 'devices';
const nowFileName = 'now.json';
const logFileName = 'log.txt';
const historyPrefix = 'history-';
const historySuffix = '.jsonl';

/// Days of history each device keeps of its own files.
const historyKeepDays = 7;

/// Log file is cut back to its newest half when it passes this size.
const logMaxBytes = 256 * 1024;

String historyFileName(DateTime localDay) =>
    '$historyPrefix${localDay.year.toString().padLeft(4, '0')}-${localDay.month.toString().padLeft(2, '0')}-${localDay.day.toString().padLeft(2, '0')}$historySuffix';
