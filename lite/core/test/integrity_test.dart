import 'dart:convert';
import 'dart:io';

import 'package:monitor_core/monitor_core.dart';
import 'package:test/test.dart';

void main() {
  late Directory tmp;
  setUp(() => tmp = Directory.systemTemp.createTempSync('integrity'));
  tearDown(() => tmp.deleteSync(recursive: true));

  void write(String rel, List<Map<String, Object?>> lines) {
    final f = File('${tmp.path}/dev/history/$rel')..createSync(recursive: true);
    f.writeAsStringSync(lines.map((l) => '${jsonEncode(l)}\n').join());
  }

  final now = DateTime.utc(2026, 11, 27, 12);
  final s = now.millisecondsSinceEpoch ~/ 1000;

  test('all green when every ended hour and day is summarized', () {
    final h = s - 5 * 3600;
    write('detail/a.jsonl', [{'t': h + 10}, {'t': h + 600}]);
    write('hourly/2026-11.jsonl', [{'t': h}, {'t': s - 30 * 3600}]);
    write('daily/2026.jsonl', [{'t': s - 40 * 3600}]);
    final r = checkHistory('${tmp.path}/dev', now, Retention.defaults);
    expect(r.problems, isEmpty);
    expect(r.line, startsWith('history ok:'));
  });

  test('missing summaries, conflict copies and the size cap are reported', () {
    final h = s - 5 * 3600;
    write('detail/a.jsonl', [{'t': h + 10}]);
    write('detail/b.jsonl', [{'t': s - 600}]); // current hour: not due yet
    write('hourly/2026-11.jsonl', [{'t': s - 30 * 3600}]);
    write('hourly/2026-11.sync-conflict-20261127-120000-ABCDEFG.jsonl', [{'t': h}]);
    final r = checkHistory('${tmp.path}/dev', now, const Retention(maxMb: 5));
    expect(r.problems.join(' | '), contains('1 ended hour has no summary'));
    expect(r.problems.join(' | '), contains('1 ended day has no daily summary'));
    expect(r.problems.join(' | '), contains('1 conflict copy'));
  });
}
