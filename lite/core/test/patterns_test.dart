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
      for (var h = 0; h < 10; h++) {'t': 1795680000 + h * 3600, 'n': 60, 'p.x': [h < 5 ? 1 : 0, h < 5 ? 1 : 0, h < 5 ? 1 : 0]},
    ];
    final up = {'x': {for (var h = 0; h < 5; h++) 1795680000 + h * 3600}};
    expect(findPatterns(const [], hourly, names: {'x': 'X'}).single.text, startsWith('Reached X 50 %'));
    // Only 5 hours up: under the 6 needed, so nothing is claimed.
    expect(findPatterns(const [], hourly, names: {'x': 'X'}, upHours: up), isEmpty);
    up['x']!.add(1795680000 + 5 * 3600);
    expect(findPatterns(const [], hourly, names: {'x': 'X'}, upHours: up).single.text, startsWith('Reached X 83 %'));
  });
}
