import 'dart:convert';
import 'dart:io';

import 'package:monitor_core/monitor_core.dart';

/// dart run tool/gaps.dart <Monitor folder> [days] [id=offsetHours ...]
/// Prints each device's history gaps with what the files say about them.
void main(List<String> args) {
  final root = args[0];
  final days = args.length > 1 ? int.parse(args[1]) : 7;
  final offsets = <String, Duration>{
    for (final a in args.skip(2))
      if (a.contains('=')) a.split('=')[0]: Duration(minutes: (double.parse(a.split('=')[1]) * 60).round())
  };
  final reports = SyncFolder(root).readReports();
  final kinds = {for (final r in reports.values) r.id: r.kind};
  final names = {for (final r in reports.values) r.id: r.name};
  final all = folderGaps(root, DateTime.now().toUtc(), days: days, offsets: offsets, kinds: kinds);
  for (final e in all.entries) {
    stdout.writeln('== ${names[e.key] ?? e.key} (${e.key}, ${kinds[e.key]}): ${e.value.length} gaps');
    for (final g in e.value) {
      stdout.writeln('  ${g.start.toIso8601String().substring(0, 16)} +${g.length.inMinutes}m ${g.why} ${jsonEncode({'reachable': g.reachable, 'restarts': g.restarts.take(3).toList(), 'errors': g.errors.take(2).toList()})}');
    }
  }
}
