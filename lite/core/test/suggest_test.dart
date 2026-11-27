import 'dart:io';

import 'package:monitor_core/monitor_core.dart';
import 'package:test/test.dart';

void main() {
  final now = DateTime.utc(2026, 11, 27, 18);
  Report rep(String id, String kind, {String? machine, String? name, List<Map<String, Object?>> readings = const [], String? node, DateTime? t}) => Report.tryParse({
        'v': 2, 'id': id, 'name': name ?? id, 'kind': kind, 't': (t ?? now).millisecondsSinceEpoch ~/ 1000, 'next': 60,
        if (machine != null) 'machine': machine, 'readings': readings, if (node != null) 'node': {'id': id, 'type': node},
      })!;
  final reports = {
    'desk': rep('desk', 'linux', machine: 'pc', name: 'wc-desktop', t: now.subtract(const Duration(hours: 2))),
    'alpha': rep('alpha', 'linux', machine: 'pc', name: 'wc-alpha'),
    'pixel': rep('pixel', 'android', name: 'wc-pixel'),
  };
  final ups = rep('usb-0764-0601-port15', 'thing', name: 'CyberPower OR500', node: 'ups', readings: [
    {'key': 'ups.identity', 'value': 'USB 0764:0601, serial CTHGT2000123'},
    {'key': 'ups.maker', 'value': 'CPS'},
  ]);
  final things = {ups.id: Thing(ups, 'alpha')};

  test('one per machine, the UPS under its host, with lasting identities', () {
    final s = suggestLinks(reports, things, const [], now);
    final pc = s.firstWhere((x) => x.match == 'machine:pc');
    expect(pc.observes, 'alpha');
    expect(pc.also, ['desk']);
    expect(pc.name, 'wc-alpha (also wc-desktop)');
    expect(s.firstWhere((x) => x.observes == 'pixel').type, 'mobile');
    final u = s.firstWhere((x) => x.type == 'ups');
    expect(u.match, 'usb:0764:0601:CTHGT2000123');
    expect(u.parentObserves, 'alpha');
    expect(u.props['maker']!.value, 'CPS');
    expect(s, hasLength(3));
  });

  test('accepted items are not offered again; a moved thing is relinked, not duplicated', () {
    final dir = Directory.systemTemp.createTempSync('suggest');
    addTearDown(() => dir.deleteSync(recursive: true));
    final store = ModelStore(dir.path, 'alpha');
    for (final x in suggestLinks(reports, things, const [], now)) {
      acceptLink(store, x, now: now);
    }
    List<Item> items() => [for (final id in store.ids()) store.get(id)!];
    expect(items(), hasLength(3));
    final upsItem = items().firstWhere((i) => i.type == 'ups');
    expect(store.get(upsItem.parent!)!.observes, 'alpha');
    // The desktop system boots instead: still the same machine, nothing new.
    expect(suggestLinks(reports, things, items(), now), isEmpty);

    // Same UPS now written under a new id.
    final moved = rep('usb-0764-0601-port9', 'thing', name: 'CyberPower OR500', node: 'ups', readings: [
      {'key': 'ups.identity', 'value': 'USB 0764:0601, serial CTHGT2000123'},
    ]);
    final s = suggestLinks(reports, {moved.id: Thing(moved, 'desk')}, items(), now);
    expect(s.single.relinkItem, upsItem.id);
    acceptLink(store, s.single, now: now.add(const Duration(minutes: 1)));
    expect(store.get(upsItem.id)!.observes, 'usb-0764-0601-port9');
    expect(items(), hasLength(3));
  });
}
