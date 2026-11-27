// Row 17: the system at a past moment.
// Usage: dart run tool/timeline.dart <folder> <time, e.g. 2026-11-27T03:30Z> [more times]
import 'dart:io';

import 'package:monitor_core/monitor_core.dart';

void main(List<String> args) {
  if (args.length < 2) {
    stderr.writeln('usage: dart run tool/timeline.dart <folder> <time> [time ...]');
    exit(2);
  }
  final names = {for (final r in SyncFolder(args.first).readReports().values) r.id: r.name};
  String n(String id) => names[id] ?? id;
  for (final a in args.skip(1)) {
    final t = DateTime.parse(a).toUtc();
    final m = topologyAt(args.first, t);
    stdout.writeln('== ${t.toIso8601String()}${m.fromHourly ? ' (hourly)' : ''}: up ${m.up.map(n).join(', ')}');
    for (final d in m.devices.values.where((d) => d.up)) {
      final reached = [for (final e in d.reach.entries) if (e.value) n(e.key)];
      stdout.writeln('  ${n(d.id)}: reaches ${reached.isEmpty ? 'nobody' : reached.join(', ')}'
          '${d.things.isEmpty ? '' : '; things ${d.things.join(', ')}'}'
          '${d.containers.isEmpty ? '' : '; containers ${d.containers.entries.map((e) => '${e.key}=${e.value}').join(', ')}'}');
    }
  }
}
