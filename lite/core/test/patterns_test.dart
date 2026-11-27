import 'dart:io';

import 'package:monitor_core/monitor_core.dart';
import 'package:test/test.dart';

void main() {
  Map<String, Object?> d(int min, num b, {int c = 0, int? s}) => {'t': 1795680000 + min * 60, 'b': b, 'c': c, if (s != null) 's': s};

  test('battery drain and charge rates, by screen, need an hour of history', () {
    final lines = [
      for (var m = 0; m <= 120; m += 15) d(m, 90 - m / 60 * 2, s: 0), // 2 %/h screen off, 2 h
      for (var m = 135; m <= 165; m += 15) d(m, 80 - (m - 135) / 60 * 20, s: 1), // only 30 min: not shown
      for (var m = 180; m <= 300; m += 15) d(m, 50 + (m - 180) / 60 * 30, c: 1), // charges 30 %/h
    ];
    final p = {for (final x in findPatterns(lines, const [])) x.key: x};
    expect(p['battery.drain.off']!.value, closeTo(2, 0.01));
    expect(p['battery.drain.off.lasts']!.estimated, isTrue);
    expect(p['battery.drain.off.lasts']!.value, closeTo(50, 0.1));
    expect(p.containsKey('battery.drain.on'), isFalse);
    expect(p['battery.charge']!.value, closeTo(30, 0.01));
  });

  test('a full battery on mains is not "drain"', () {
    final lines = [for (var m = 0; m <= 240; m += 15) d(m, 100)];
    expect(findPatterns(lines, const []), isEmpty);
  });

  test('reach leaves out hours the other device was off', () {
    final hourly = [
      for (var h = 0; h < 10; h++)
        {
          't': 1795680000 + h * 3600,
          'n': 60,
          'p.x': [h < 5 ? 1 : 0, h < 5 ? 1 : 0, h < 5 ? 1 : 0]
        },
    ];
    final up = {
      'x': {for (var h = 0; h < 5; h++) 1795680000 + h * 3600}
    };
    expect(findPatterns(const [], hourly, names: {'x': 'X'}).single.text, startsWith('Reached X 50 %'));
    // Only 5 hours up: under the 6 needed, so nothing is claimed.
    expect(findPatterns(const [], hourly, names: {'x': 'X'}, upHours: up), isEmpty);
    up['x']!.add(1795680000 + 5 * 3600);
    expect(findPatterns(const [], hourly, names: {'x': 'X'}, upHours: up).single.text, startsWith('Reached X 83 %'));
  });

  test('pattern values are kept one line per day and pruned with daily history', () {
    final dir = Directory.systemTemp.createTempSync('pat');
    addTearDown(() => dir.deleteSync(recursive: true));
    const p = Pattern(key: 'battery.drain.off', group: 'Battery', text: '', value: 2.345, unit: '%/h', hours: 5);
    final day1 = DateTime(2026, 11, 20, 9), day2 = DateTime(2026, 11, 21, 9);
    recordPatterns(dir.path, [p], day1);
    recordPatterns(dir.path, [const Pattern(key: 'battery.drain.off', group: 'Battery', text: '', value: 3, hours: 6)], day1.add(const Duration(hours: 6)));
    recordPatterns(dir.path, [p, const Pattern(key: 'x', group: 'g', text: 'no value', hours: 2)], day2);
    final lines = readPatternHistory(dir.path, day2);
    expect(lines.length, 2); // the second write on day 1 replaced the first
    expect(seriesOf(lines, 'battery.drain.off'), [3, 2.35]);
    expect(lines.last.containsKey('x'), isFalse);
    // Old years go with the daily retention.
    final old = File('${dir.path}/history/patterns/2019.jsonl')..writeAsStringSync('{"t":1}\n');
    HistoryWriter(dir.path, Directory.systemTemp.createTempSync('spool').path).prune(day2, Retention.fromJson({'dailyDays': 365}));
    expect(old.existsSync(), isFalse);
    expect(readPatternHistory(dir.path, day2).length, 2);
  });

  test('UPS: power cuts with their length, usual load, runtime when full (estimated)', () {
    Map<String, Object?> u(int min, {int onBattery = 0, num charge = 100, num runtime = 70.5, num load = 13}) =>
        {'t': 1795680000 + min * 60, 'ups.onBattery': onBattery, 'ups.charge': charge, 'ups.runtime': runtime, 'ups.load': load};
    final lines = [
      for (var m = 0; m < 600; m++)
        // Two cuts: 5 min at minute 100, 12 min at minute 300.
        if (m >= 100 && m < 105 || m >= 300 && m < 312) u(m, onBattery: 1, charge: 90, runtime: 40) else u(m),
    ];
    final hourly = [
      for (var h = 0; h < 10; h++)
        {
          't': 1795680000 + h * 3600,
          'n': 60,
          'ups.load': [10, 13, 20]
        }
    ];
    final p = {for (final x in findPatterns(lines, hourly)) x.key: x};
    expect(p['ups.cuts']!.value, 2);
    expect(p['ups.cuts']!.text, 'On battery 2 times, 17 min in all, longest 12 min (over 10.0 h)');
    expect(p['ups.load.usual']!.text, startsWith('UPS load is usually 13 %, highest 20 %'));
    expect(p['ups.runtime.full']!.value, 70.5);
    expect(p['ups.runtime.full']!.estimated, isTrue);
    final calm = {
      for (final x in findPatterns([for (var m = 0; m < 600; m++) u(m)], const [])) x.key: x
    };
    expect(calm['ups.cuts']!.text, startsWith('No power cuts'));
  });

  test('a UPS history line says whether it is on battery', () {
    final r = Report(id: 'u', name: 'UPS', kind: 'thing', time: DateTime.utc(2026), readings: [Reading(key: 'ups.status', value: 'battery, low')]);
    expect(r.historyLine()['ups.onBattery'], 1);
  });

  test('conditional patterns: drain with the app open, data by network type, temperature by load, UPS runtime by load', () {
    final t0 = DateTime.utc(2026, 11, 20).millisecondsSinceEpoch ~/ 1000;
    final lines = <Map<String, Object?>>[];
    var b = 90.0;
    for (var i = 0; i < 320; i++) {
      final w = i < 120 ? 1 : 0;
      b -= w == 1 ? 0.05 : 0.01; // 3 %/h watched, 0.6 %/h quiet
      lines.add({'t': t0 + i * 60, 'b': b, 'c': 0, 'w': w, 'nt': i < 180 ? 1 : 2, 'rx': 1000, 'tx': 0,
        'cpu': i.isEven ? 5 : 80, 'temp.cpu': i.isEven ? 40 : 70,
        'ups.load': i < 120 ? 15 : 35, 'ups.runtime': i < 120 ? 70 : 30, 'ups.charge': 100});
    }
    final p = {for (final x in findPatterns(lines, const [])) x.key: x};
    expect(p['battery.drain.watched']!.value, closeTo(3, 0.1));
    expect(p['battery.drain.quiet']!.value, closeTo(0.6, 0.05));
    expect(p['net.perDay.wifi'], isNotNull);
    expect(p['net.perDay.mobile'], isNotNull);
    expect(p['temp.cpu.byLoad']!.text, contains('40 °C when idle and 70 °C when busy'));
    expect(p['ups.runtime.byLoad']!.text, contains('70 min at 10-20 % load, 30 min at 30-40 % load'));
  });

  test('container restarts come from the host events', () {
    final now = DateTime.utc(2026, 11, 27);
    final ev = [for (var i = 0; i < 3; i++) Event(now.subtract(Duration(days: i)), 'container.started', subject: 'ctr-x-pihole')];
    expect(restartsPattern(ev, 'ctr-x-pihole', now).single.text, startsWith('Started 3 times in 7 days'));
    expect(restartsPattern(ev, 'ctr-x-caddy', now).single.text, 'Not restarted in 7 days');
  });
}
