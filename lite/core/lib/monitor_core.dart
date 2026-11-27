/// Shared logic for Monitor Lite.
///
/// Every device writes its own small files into one Syncthing-shared folder:
///   devices/<id>/now.json              latest readings, overwritten each cycle
///   devices/<id>/history/...           detail per hour, hourly and daily summaries
///   devices/<id>/log.txt               the app's own log (kept small)
/// Nothing else moves data between devices. See lite/FORMAT.md.
library;

export 'src/report.dart';
export 'src/topology.dart';
export 'src/status.dart';
export 'src/folder.dart';
export 'src/version.dart';
export 'src/update.dart';
export 'src/format.dart';
export 'src/history.dart';
export 'src/patterns.dart';
export 'src/config.dart';
export 'src/control.dart';
