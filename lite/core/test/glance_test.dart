import 'package:monitor_core/monitor_core.dart';
import 'package:test/test.dart';

void main() {
  final now = DateTime.utc(2026, 11, 27, 21);
  Thing ups(String status, {num charge = 100, num runtime = 70.5, DateTime? t}) => Thing(
      Report(id: 'usb-0764-0601-port15', name: 'CyberPower OR500', kind: 'thing', time: t ?? now, nextSeconds: 60, node: NodeDecl(id: 'usb-0764-0601-port15', type: 'ups', parent: 'alpha'), readings: [
        Reading(key: 'ups.status', value: status),
        Reading(key: 'ups.charge', value: charge, unit: '%'),
        Reading(key: 'ups.runtime', value: runtime, unit: 'min'),
      ]),
      'alpha');

  test('UPS on one line, no submenus', () {
    final g = glance({'u': ups('mains')}, now: now).single;
    expect(g.label, 'UPS');
    expect(g.text, 'On mains, 100 %, 70.5 min');
    expect(g.level, Level.ok);
  });
  test('on battery is a warning, low is a fault', () {
    expect(glance({'u': ups('battery', charge: 64, runtime: 22)}, now: now).single.text, 'On battery, 64 %, 22 min left');
    expect(glance({'u': ups('battery, low', charge: 20)}, now: now).single.level, Level.fault);
  });
  test('stale UPS says so instead of old numbers', () {
    final g = glance({'u': ups('mains', t: now.subtract(const Duration(hours: 1)))}, now: now).single;
    expect(g.level, Level.inactive);
    expect(g.text, startsWith('not reported'));
  });
}
