import 'dart:io';

import 'package:monitor_core/monitor_core.dart';
import 'package:test/test.dart';

void main() {
  final t0 = DateTime.utc(2026, 11, 27, 12);
  Report peer(String id, DateTime seen) => Report(id: id, name: 'n-$id', kind: 'linux', time: seen, nextSeconds: 60);
  Report ups(String status) => Report(id: 'ups', name: 'UPS', kind: 'thing', time: t0, nextSeconds: 60, readings: [Reading(key: 'ups.status', value: status)]);
  Report ctr(String state, {String? health}) => Report(id: 'ctr-pihole', name: 'pihole', kind: 'thing', time: t0, nextSeconds: 60, readings: [
        Reading(key: 'ct.state', value: state),
        if (health != null) Reading(key: 'ct.health', value: health),
      ]);

  test('first look is never an event', () {
    final now = EventState.of(app: '2.6.7', others: {'a': peer('a', t0)}, things: [ups('mains')], now: t0);
    expect(diffEvents(EventState(), now, t0), isEmpty);
  });

  test('power cut, container stop, update and a peer going offline, once each', () {
    final a = EventState.of(app: '2.6.6', others: {'a': peer('a', t0)}, things: [ups('mains'), ctr('running')], now: t0);
    final t1 = t0.add(const Duration(minutes: 10));
    final b = EventState.of(app: '2.6.7', others: {'a': peer('a', t0)}, things: [ups('battery'), ctr('exited')], now: t1);
    final ev = diffEvents(a, b, t1);
    expect(ev.map((e) => e.type), unorderedEquals(['app.updated', 'peer.offline', 'power.battery', 'container.stopped']));
    expect(diffEvents(b, b, t1), isEmpty);
    final c = EventState.of(app: '2.6.7', others: {'a': peer('a', t1)}, things: [ups('mains'), ctr('running', health: 'unhealthy')], now: t1);
    expect(diffEvents(b, c, t1).map((e) => e.type), unorderedEquals(['peer.online', 'power.mains', 'container.started', 'container.unhealthy']));
  });

  test('right after waking, peer freshness is not trusted', () {
    final a = EventState.of(app: '1', others: {'a': peer('a', t0)}, things: const [], now: t0);
    final t1 = t0.add(const Duration(hours: 8));
    final b = EventState.of(app: '1', others: {'a': peer('a', t0)}, things: const [], now: t1);
    expect(diffEvents(a, b, t1, peers: false), isEmpty);
  });

  test('written per month and read back across devices in order', () {
    final d = Directory.systemTemp.createTempSync('events');
    addTearDown(() => d.deleteSync(recursive: true));
    appendEvents('${d.path}/devices/x', [Event(DateTime.utc(2026, 10, 31, 23), 'peer.offline', subject: 'y')]);
    appendEvents('${d.path}/devices/y', [Event(DateTime.utc(2026, 11, 1, 1), 'app.updated', detail: '1 -> 2')]);
    final all = readEvents(d.path, DateTime.utc(2026, 10, 1), DateTime.utc(2026, 11, 30));
    expect(all.map((e) => e.device), ['x', 'y']);
    expect(File('${d.path}/devices/x/events/2026-10.jsonl').existsSync(), isTrue);
    pruneEvents('${d.path}/devices/x', DateTime.utc(2027, 3, 1), 30);
    expect(File('${d.path}/devices/x/events/2026-10.jsonl').existsSync(), isFalse);
  });
}
