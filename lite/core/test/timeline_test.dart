import 'dart:convert';
import 'dart:io';

import 'package:monitor_core/monitor_core.dart';
import 'package:test/test.dart';

void main() {
  late Directory tmp;
  setUp(() => tmp = Directory.systemTemp.createTempSync('timeline'));
  tearDown(() => tmp.deleteSync(recursive: true));

  void lines(String rel, List<Map<String, Object?>> ls) =>
      (File('${tmp.path}/devices/$rel')..createSync(recursive: true)).writeAsStringSync(ls.map((l) => '${jsonEncode(l)}\n').join());
  int s(DateTime t) => t.millisecondsSinceEpoch ~/ 1000;

  test('who was up and who reached whom at a moment, and which containers ran', () {
    final t0 = DateTime.utc(2026, 11, 27, 3);
    // server every minute until 03:40 (power cut), phone every 9 minutes all along
    lines('srv/history/detail/${detailName(t0)}', [
      for (var m = 0; m < 40; m++) {'t': s(t0.add(Duration(minutes: m))), 'p': {'phone': 1}},
    ]);
    lines('phone/history/detail/${detailName(t0)}', [
      for (var m = 0; m < 60; m += 9) {'t': s(t0.add(Duration(minutes: m))), 'p': {'srv': m < 40 ? 1 : 0}},
    ]);
    lines('srv/things/ctr-srv-pihole/history/detail/${detailName(t0)}', [
      for (var m = 0; m < 40; m++) {'t': s(t0.add(Duration(minutes: m)))},
    ]);
    final now = DateTime.utc(2026, 11, 27, 12);
    final before = topologyAt(tmp.path, t0.add(const Duration(minutes: 20)), now: now);
    expect(before.up, ['phone', 'srv']);
    expect(before.devices['srv']!.reach, {'phone': true});
    expect(before.devices['srv']!.containers, {'ctr-srv-pihole': 'running'});
    final after = topologyAt(tmp.path, t0.add(const Duration(minutes: 50)), now: now);
    expect(after.up, ['phone'], reason: 'the phone writes every 9 min, so 50 min is near its 54 min line');
    expect(after.devices['phone']!.reach, {'srv': false});
  });
}
