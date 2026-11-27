import 'dart:io';

import 'history.dart';

/// Health of one device's history on disk (roadmap row 13): under its size
/// cap, no Syncthing conflict copies, a summary for every ended hour and day
/// that has data, and how long the app's reads take.
class HistoryIntegrity {
  HistoryIntegrity(this.deviceDir, {required this.bytes, required this.capBytes, required this.problems, required this.readMs});
  final String deviceDir;
  final int bytes, capBytes;

  /// Plain sentences; empty means all green.
  final List<String> problems;

  /// readHistoryRange cost per range, in ms.
  final Map<String, int> readMs;

  bool get ok => problems.isEmpty;

  /// One log line: "history ok: 1.2 MB of 50 MB, read day 3 ms ..." or
  /// "history: <problems>".
  String get line {
    final mb = (bytes / 1048576).toStringAsFixed(1);
    final reads = readMs.entries.map((e) => '${e.key} ${e.value} ms').join(', ');
    return ok ? 'history ok: $mb MB of ${capBytes ~/ 1048576} MB, read $reads' : 'history: ${problems.join('; ')} ($mb MB, read $reads)';
  }
}

/// Checks [deviceDir] at [now]. Summary files are named in the writer's
/// local time, which a reader can't know, so the check goes by each line's
/// `t`: an hourly summary starts at the hour of the detail lines it covers
/// (within the hour before the first line), a daily one within the day
/// before the first hourly line. Hours or days that may not have been
/// summarized yet (the last 75 minutes / 26 hours) are skipped, as is
/// anything older than the writer catches up on (2 days of hours, 7 days of
/// days) when it was off.
HistoryIntegrity checkHistory(String deviceDir, DateTime now, Retention r) {
  final sep = Platform.pathSeparator;
  final problems = <String>[];
  final h = Directory('$deviceDir${sep}history');
  final nowS = now.millisecondsSinceEpoch ~/ 1000;

  final conflicts = <String>[];
  var bytes = 0;
  if (Directory(deviceDir).existsSync()) {
    for (final f in Directory(deviceDir).listSync(recursive: true).whereType<File>()) {
      final name = f.uri.pathSegments.last;
      if (name.contains('.sync-conflict-')) conflicts.add(f.path.substring(deviceDir.length + 1));
      if (f.path.contains('${sep}history$sep')) bytes += f.lengthSync();
    }
  }
  final cap = r.maxMb * 1048576;
  if (bytes > cap) problems.add('history is ${(bytes / 1048576).toStringAsFixed(1)} MB, over its ${r.maxMb} MB cap');
  if (conflicts.isNotEmpty) problems.add('${conflicts.length} conflict ${conflicts.length == 1 ? 'copy' : 'copies'} (${conflicts.take(3).join(', ')})');

  List<Map<String, Object?>> linesIn(String kind) {
    final d = Directory('${h.path}$sep$kind');
    if (!d.existsSync()) return const [];
    return [
      for (final f in d.listSync().whereType<File>())
        if (!f.uri.pathSegments.last.contains('.sync-conflict-')) ...readLines(f),
    ];
  }

  final hourly = linesIn('hourly').map((l) => (l['t']! as num).toInt()).toSet().toList()..sort();
  final daily = linesIn('daily').map((l) => (l['t']! as num).toInt()).toSet().toList()..sort();
  bool covered(List<int> starts, int t, int span) => starts.any((s) => s <= t && t < s + span);

  final missingHours = <int>[];
  final detailDir = Directory('${h.path}${sep}detail');
  if (detailDir.existsSync()) {
    final files = [
      for (final f in detailDir.listSync().whereType<File>())
        if (!f.uri.pathSegments.last.contains('.sync-conflict-')) readLines(f),
    ].where((ls) => ls.isNotEmpty).toList();
    // The writer summarizes an hour on its next run, so the newest hour of
    // a device that is off now (one half of a dual boot) is not due yet.
    final newest = files.isEmpty ? 0 : files.map((ls) => (ls.last['t']! as num).toInt()).reduce((a, b) => a > b ? a : b);
    for (final ls in files) {
      final first = (ls.first['t']! as num).toInt();
      final last = (ls.last['t']! as num).toInt();
      if (last == newest || nowS - last < 75 * 60 || nowS - first > 2 * 86400) continue;
      if (!covered(hourly, first, 3600)) missingHours.add(first);
    }
  }
  if (missingHours.isNotEmpty) {
    missingHours.sort();
    problems.add('${missingHours.length} ended ${missingHours.length == 1 ? 'hour has' : 'hours have'} no summary '
        '(first ${DateTime.fromMillisecondsSinceEpoch(missingHours.first * 1000, isUtc: true).toIso8601String().substring(0, 13)}Z)');
  }

  final missingDays = <int>{};
  for (final t in hourly) {
    if (nowS - t < 26 * 3600 || nowS - t > 7 * 86400) continue;
    if (!covered(daily, t, 86400)) missingDays.add(t ~/ 86400);
  }
  if (missingDays.isNotEmpty) {
    problems.add('${missingDays.length} ended ${missingDays.length == 1 ? 'day has' : 'days have'} no daily summary');
  }

  final readMs = <String, int>{};
  for (final range in HistoryRange.values) {
    final sw = Stopwatch()..start();
    readHistoryRange(deviceDir, range, now);
    readMs[range.name] = sw.elapsedMilliseconds;
  }
  if (readMs['year']! > 50) problems.add('a year view took ${readMs['year']} ms to read (budget 50 ms)');

  return HistoryIntegrity(deviceDir, bytes: bytes, capBytes: cap, problems: problems, readMs: readMs);
}
