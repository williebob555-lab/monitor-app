import 'package:monitor_core/monitor_core.dart';

Map<String, List<Map<String, Object?>>> readMachineHistory(
  (Map<String, String>, HistoryRange, DateTime) request,
) {
  final (directories, range, now) = request;
  final histories = <String, List<Map<String, Object?>>>{};
  for (final entry in directories.entries) {
    try {
      histories[entry.key] = readHistoryRange(entry.value, range, now);
    } on Exception {
      histories[entry.key] = [];
    }
  }
  return histories;
}
