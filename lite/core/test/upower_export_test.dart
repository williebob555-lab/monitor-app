import 'dart:io';

import 'package:monitor_core/src/agent/things.dart';
import 'package:test/test.dart';

/// What `upower -i` prints for a USB UPS (UPower 1.91), behind the
/// exporter's "exported:" line (packaging/linux/upower-export.sh).
String export(int t, {String state = 'fully-charged', String warning = 'none', String pct = '100%'}) => '''exported: $t
  native-path:          hiddev0
  vendor:               CPS
  model:                OR500ERM1U
  power supply:         yes
  updated:              Thu 27 Nov 2026 03:00:00 AM EST (5 seconds ago)
  ups
    present:             yes
    state:               $state
    warning-level:       $warning
    time to empty:       1.2 hours
    percentage:          $pct
''';

void main() {
  final now = DateTime.utc(2026, 11, 27, 3);
  final t = now.millisecondsSinceEpoch ~/ 1000;
  late Directory dir;
  setUp(() => dir = Directory.systemTemp.createTempSync('upower'));
  tearDown(() => dir.deleteSync(recursive: true));
  File file(String text) => File('${dir.path}/ups.txt')..writeAsStringSync(text);
  Map<String, Object?> values(List r) => {for (final x in r) x.key as String: x.value};

  test('a fresh export gives mains, charge, runtime (estimated), maker and model; load stays unknown', () {
    final got = Things.readUpowerExport(file(export(t - 30)).path, now)!;
    expect(got.$2, isNull);
    final r = Things.upsFromUpower(got.$1!, source: 'UPower on the host (exported file)');
    final v = values(r);
    expect(v['ups.status'], 'mains');
    expect(v['ups.charge'], 100);
    expect(v['ups.runtime'], 72);
    expect(r.firstWhere((x) => x.key == 'ups.runtime').src, 'estimated');
    expect(r.firstWhere((x) => x.key == 'ups.load').src, 'unknown');
    expect(v['ups.maker'], 'CPS');
    expect(v['ups.model'], 'OR500ERM1U');
    expect(v['ups.source'], 'UPower on the host (exported file)');
  });

  test('on battery, and low when UPower warns', () {
    var r = Things.upsFromUpower(Things.readUpowerExport(file(export(t, state: 'discharging', pct: '64%')).path, now)!.$1!);
    expect(values(r)['ups.status'], 'battery');
    expect(r.first.level, 'warn');
    r = Things.upsFromUpower(Things.readUpowerExport(file(export(t, state: 'discharging', warning: 'critical', pct: '18%')).path, now)!.$1!);
    expect(values(r)['ups.status'], 'battery, low');
    expect(r.first.level, 'fault');
  });

  test('an old export, no UPS, or no file is never shown as current', () {
    expect(Things.readUpowerExport(file(export(t - 600)).path, now)!.$2, contains('10 min old'));
    expect(Things.readUpowerExport(file('exported: $t\n').path, now)!.$2, 'UPower on the host sees no UPS');
    expect(Things.readUpowerExport(file('garbage').path, now)!.$2, contains('no "exported" time'));
    expect(Things.readUpowerExport('${dir.path}/missing.txt', now), isNull);
  });
}
