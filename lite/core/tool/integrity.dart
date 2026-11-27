// Row 13: history integrity over a whole Monitor folder.
// Usage: dart run tool/integrity.dart <folder>
import 'dart:io';

import 'package:monitor_core/monitor_core.dart';

void main(List<String> args) {
  if (args.isEmpty) {
    stderr.writeln('usage: dart run tool/integrity.dart <folder>');
    exit(2);
  }
  final root = args.first;
  final r = Retention.load(root);
  final now = DateTime.now().toUtc();
  var bad = 0;
  final devices = Directory('$root${Platform.pathSeparator}devices');
  for (final d in devices.listSync().whereType<Directory>().toList()..sort((a, b) => a.path.compareTo(b.path))) {
    final res = checkHistory(d.path, now, r);
    if (!res.ok) bad++;
    stdout.writeln('${d.uri.pathSegments.where((s) => s.isNotEmpty).last}: ${res.line}');
  }
  stdout.writeln(bad == 0 ? 'all green' : '$bad device(s) need a look');
  exit(bad == 0 ? 0 : 1);
}
