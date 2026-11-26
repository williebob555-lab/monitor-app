import 'dart:io';

import 'package:monitor_core/monitor_core.dart';
import 'package:test/test.dart';

void main() {
  late Directory tmp;
  late HistoryWriter w;
  setUp(() {
    tmp = Directory.systemTemp.createTempSync('hist');
    w = HistoryWriter('${tmp.path}/dev', '${tmp.path}/spool');
  });
  tearDown(() => tmp.deleteSync(recursive: true));

  int s(DateTime d) => d.millisecondsSinceEpoch ~/ 1000;

  test('readings wait in the local spool and reach the shared folder every 15 minutes', () {
    final t0 = DateTime(2026, 11, 26, 10, 0);
    for (var i = 0; i < 14; i++) {
      final t = t0.add(Duration(minutes: i));
      w.add({'t': s(t), 'cpu': i}, t);
    }
    // First add of the hour flushes; the rest wait.
    expect(readLines(File('${tmp.path}/dev/history/detail/${detailName(t0)}')).length, 1);
    final t15 = t0.add(const Duration(minutes: 15));
    w.add({'t': s(t15), 'cpu': 15}, t15);
    expect(readLines(File('${tmp.path}/dev/history/detail/${detailName(t0)}')).length, 15);
  });

  test('an ended hour becomes one summary line; an ended day one daily line', () {
    final t0 = DateTime(2026, 11, 25, 23, 0);
    for (var i = 0; i < 60; i += 5) {
      final t = t0.add(Duration(minutes: i));
      w.add({'t': s(t), 'cpu': i.toDouble(), 'p': {'x': 1}}, t);
    }
    final next = DateTime(2026, 11, 26, 0, 1);
    w.add({'t': s(next), 'cpu': 1}, next);
    final hourly = readLines(File('${tmp.path}/dev/history/hourly/${hourlyName(t0)}'));
    expect(hourly.length, 1);
    expect(hourly.first['cpu'], [0.0, 27.5, 55.0]);
    expect(hourly.first['p.x'], [1.0, 1.0, 1.0]);
    expect(hourly.first['n'], 12);
    final daily = readLines(File('${tmp.path}/dev/history/daily/${dailyName(t0)}'));
    expect(daily.length, 1);
    expect(daily.first['cpu'], [0.0, 27.5, 55.0]);
    expect(chartValue(daily.first, 'cpu'), 27.5);
  });

  test('old files are deleted by age, then by size cap, detail first', () {
    final now = DateTime(2026, 11, 26, 12);
    final d = Directory('${tmp.path}/dev/history/detail')..createSync(recursive: true);
    final h = Directory('${tmp.path}/dev/history/hourly')..createSync(recursive: true);
    File('${d.path}/${detailName(now.subtract(const Duration(days: 9)))}').writeAsStringSync('{"t":1}\n');
    final keep = File('${d.path}/${detailName(now.subtract(const Duration(hours: 2)))}')..writeAsStringSync('{"t":1}\n');
    File('${d.path}/x.sync-conflict-20261126.jsonl').writeAsStringSync('junk');
    final hourly = File('${h.path}/${hourlyName(now)}')..writeAsStringSync('{"t":1}\n');
    w.prune(now, Retention.defaults);
    expect(d.listSync().map((e) => e.uri.pathSegments.last), [detailName(now.subtract(const Duration(hours: 2)))]);
    // Cap: make detail big, cap at 5 MB -> detail goes, hourly stays.
    keep.writeAsBytesSync(List.filled(6 * 1024 * 1024, 32));
    w.prune(now, const Retention(maxMb: 5));
    expect(keep.existsSync(), isFalse);
    expect(hourly.existsSync(), isTrue);
  });

  test('retention settings are read leniently and clamped', () {
    final r = Retention.fromJson({'detailDays': 1000, 'hourlyDays': 'x', 'maxMb': 1});
    expect(r.detailDays, 90);
    expect(r.hourlyDays, Retention.defaults.hourlyDays);
    expect(r.maxMb, 5);
    r.save(tmp.path);
    expect(Retention.load(tmp.path).detailDays, 90);
  });
}
