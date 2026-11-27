import 'package:monitor_core/monitor_core.dart';
import 'package:test/test.dart';

void main() {
  // Local times, as the writer names them.
  final now = DateTime(2026, 11, 27, 3, 30);
  List<Map<String, Object?>> hourly(int days, double cpu) => [
        for (var d = 1; d <= days; d++)
          for (var h = 0; h < 6; h++) {'t': DateTime(2026, 11, 27 - d, h).millisecondsSinceEpoch ~/ 1000, 'cpu': [cpu, cpu, cpu + 5]},
      ];
  List<Map<String, Object?>> recent(double cpu) => [
        for (var m = 0; m < 30; m++) {'t': now.subtract(Duration(minutes: m)).millisecondsSinceEpoch ~/ 1000, 'cpu': cpu},
      ];

  test('a busy night against quiet nights is flagged, with the reason', () {
    final r = findUnusual(hourly(5, 8), recent(70), now);
    expect(r.single.key, 'unusual.cpu');
    expect(r.single.value, 'CPU 70 %, usually 8 % at night');
    expect(r.single.level, 'warn');
  });

  test('never on less than 3 days, never for a small rise', () {
    expect(findUnusual(hourly(2, 8), recent(70), now), isEmpty);
    expect(findUnusual(hourly(5, 8), recent(20), now), isEmpty);
  });

  test('at most 3 new flags a day; a standing flag stays', () {
    final lim = UnusualLimiter();
    Reading f(String k) => Reading(key: k, value: 'x', level: 'warn');
    expect(lim.apply([f('a'), f('b'), f('c'), f('d')], now).length, 3);
    expect(lim.apply([f('a')], now).length, 1);
    expect(lim.apply([f('a'), f('e')], now).map((r) => r.key), ['a']);
    expect(lim.apply([f('e')], now.add(const Duration(days: 1))).map((r) => r.key), ['e']);
  });
}
