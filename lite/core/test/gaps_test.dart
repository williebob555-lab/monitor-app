import 'package:monitor_core/monitor_core.dart';
import 'package:test/test.dart';

void main() {
  final from = DateTime.utc(2026, 11, 27, 0);
  final to = DateTime.utc(2026, 11, 27, 2);
  int s(int min) => from.millisecondsSinceEpoch ~/ 1000 + min * 60;

  test('a hole longer than three resting intervals is a gap; shorter is not', () {
    final times = [for (var m = 0; m <= 30; m++) s(m), s(33), for (var m = 50; m <= 60; m++) s(m)];
    final g = findGaps(times, 60, from: from, to: to);
    expect(g, hasLength(1));
    expect(g.single.length, const Duration(minutes: 17));
  });

  test('log times: with offset, Z, and old lines without one', () {
    expect(logTime('2026-11-27T09:11:22-05:00 ok', Duration.zero), DateTime.utc(2026, 11, 27, 14, 11, 22));
    expect(logTime('2026-11-27T14:11:22Z ok', const Duration(hours: -5)), DateTime.utc(2026, 11, 27, 14, 11, 22));
    expect(logTime('2026-11-27T09:11:22 ok', const Duration(hours: -5)), DateTime.utc(2026, 11, 27, 14, 11, 22));
    expect(logTime('garbage', Duration.zero), isNull);
  });

  test('others reaching the device during the hole blames the collector', () {
    final g = findGaps([s(0), s(40)], 60, from: from, to: to);
    explainGaps(g, 'aa', peerDetail: {
      'bb': [for (var m = 5; m < 35; m += 5) {'t': s(m), 'p': {'aa': 1}}],
    });
    expect(g.single.why, 'collector');
  });

  test('a boot right after the hole means the device was off', () {
    final g = findGaps([s(0), s(40)], 300, from: from, to: to);
    explainGaps(g, 'aa', peerDetail: {}, log: '2026-11-27T00:39:00Z start (BOOT_COMPLETED) app 2.6.7\n', android: true);
    expect(g.single.restarts, hasLength(1));
    expect(g.single.why, 'off');
  });

  test('Android with nobody looking and no restart: asleep', () {
    final g = findGaps([s(0), s(40)], 300, from: from, to: to);
    explainGaps(g, 'aa', peerDetail: {}, android: true);
    expect(g.single.why, 'asleep');
  });
}
