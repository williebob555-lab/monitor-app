import 'dart:convert';
import 'dart:io';

import 'package:monitor_core/monitor_core.dart';
import 'package:test/test.dart';

/// History file names are in the writer's local time. A reader on another
/// clock (Wilson's laptop in New York reading a UTC server in Docker) used
/// to miss the writer's latest hours in the 24 h view.
void main() {
  for (final offset in const [-12, -5, 0, 9, 14]) {
    test('a writer at UTC${offset >= 0 ? '+' : ''}$offset: its last hours are read on any clock', () {
      final dir = Directory.systemTemp.createTempSync('zones');
      addTearDown(() => dir.deleteSync(recursive: true));
      final now = DateTime.now().toUtc();
      final detail = Directory('${dir.path}/history/detail')..createSync(recursive: true);
      final hourly = Directory('${dir.path}/history/hourly')..createSync(recursive: true);
      final want = <int>[];
      for (var h = 0; h < 3; h++) {
        final t = now.subtract(Duration(hours: h, minutes: 1));
        final wall = t.add(Duration(hours: offset)); // the writer's clock face
        final secs = t.millisecondsSinceEpoch ~/ 1000;
        want.add(secs);
        File('${detail.path}/${detailName(DateTime(wall.year, wall.month, wall.day, wall.hour))}').writeAsStringSync('${jsonEncode({'t': secs, 'cpu': h})}\n');
        File('${hourly.path}/${hourlyName(wall)}').writeAsStringSync(
            '${jsonEncode({
                  't': secs,
                  'n': 1,
                  'cpu': [h, h, h]
                })}\n',
            mode: FileMode.append);
      }
      expect(readHistoryRange(dir.path, HistoryRange.day, now).map((l) => l['t']), unorderedEquals(want));
      expect(readHistoryRange(dir.path, HistoryRange.week, now).map((l) => l['t']), unorderedEquals(want));
      expect(readDetail(dir.path, now).map((l) => l['t']), unorderedEquals(want));
      expect(readHourly(dir.path, now).map((l) => l['t']), unorderedEquals(want));
    });
  }
}
