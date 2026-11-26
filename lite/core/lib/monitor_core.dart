/// Shared logic for Monitor Lite.
///
/// Every device writes its own small files into one Syncthing-shared folder:
///   devices/<id>/now.json              latest readings, overwritten each cycle
///   devices/<id>/history-YYYY-MM-DD.jsonl  one compact line per cycle
///   devices/<id>/log.txt               the app's own log (kept small)
/// Nothing else moves data between devices. See lite/FORMAT.md.
library;

export 'src/report.dart';
export 'src/status.dart';
export 'src/folder.dart';
export 'src/version.dart';
export 'src/update.dart';
export 'src/format.dart';
export 'src/config.dart';
