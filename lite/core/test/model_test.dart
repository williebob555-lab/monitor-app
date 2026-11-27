import 'dart:convert';
import 'dart:io';

import 'package:monitor_core/monitor_core.dart';
import 'package:test/test.dart';

void main() {
  late Directory d;
  setUp(() => d = Directory.systemTemp.createTempSync('model'));
  tearDown(() => d.deleteSync(recursive: true));

  Item ups() => Item(id: 'ups-1', type: 'ups', name: 'CyberPower OR500', state: 'owned', observes: 'usb-0764-0601-port15', props: {
        'height': Prop(1, unit: 'U', src: 'declared'),
        'load': Prop(13, unit: '%', src: 'measured'),
      }, ports: [
        {'id': 'out1', 'kind': 'nema-5-15', 'dir': 'out'}
      ]);

  test('written on one device, read back unchanged on another', () {
    final laptop = ModelStore(d.path, 'laptop');
    final w = laptop.put(ups(), now: DateTime.utc(2026, 11, 27, 12));
    final pixel = ModelStore(d.path, 'pixel');
    final r = pixel.get('ups-1')!;
    expect(jsonEncode(r.toJson()), jsonEncode(w.toJson()));
    expect(r.rev, 1);
    expect(r.by, 'laptop');
    expect(r.props['load']!.src, 'measured');
    expect(r.ports.single['kind'], 'nema-5-15');
  });

  test('unknown fields survive an edit', () {
    final s = ModelStore(d.path, 'a');
    s.put(ups()..extra['futureField'] = {'x': 1});
    final it = s.get('ups-1')!..name = 'UPS';
    s.put(it);
    expect(s.get('ups-1')!.extra['futureField'], {'x': 1});
    expect(s.get('ups-1')!.rev, 2);
  });

  test('retire keeps the item and its history; asOf goes back in time', () {
    final s = ModelStore(d.path, 'a');
    s.put(ups(), now: DateTime.utc(2026, 11, 1));
    s.put(s.get('ups-1')!..name = 'renamed', now: DateTime.utc(2026, 11, 10));
    s.retire('ups-1', now: DateTime.utc(2026, 11, 20));
    expect(s.list(), isEmpty);
    expect(s.list(withRetired: true).single.state, 'retired');
    expect(s.asOf(DateTime.utc(2026, 11, 5)).single.name, 'CyberPower OR500');
    expect(s.asOf(DateTime.utc(2026, 11, 15)).single.name, 'renamed');
    expect(s.asOf(DateTime.utc(2026, 10, 1)), isEmpty);
    expect(s.history('ups-1'), hasLength(3));
  });

  test('a Syncthing conflict copy: newest wins, loser kept in history, copy removed', () {
    final a = ModelStore(d.path, 'a');
    a.put(ups(), now: DateTime.utc(2026, 11, 27, 10));
    // Device b wrote a newer version that lost the file race.
    final newer = a.get('ups-1')!.copy()
      ..name = 'from b'
      ..rev = 2
      ..by = 'b'
      ..time = DateTime.utc(2026, 11, 27, 11);
    File('${a.itemsPath}/ups-1/item.sync-conflict-20261127-110000-ABCDEFG.json').writeAsStringSync(jsonEncode(newer.toJson()));
    expect(a.get('ups-1')!.name, 'from b'); // readers already pick it
    expect(a.conflicts(), hasLength(1));
    expect(SyncFolder(d.path).health().conflicts, hasLength(1));
    expect(a.resolveConflicts(), 1);
    expect(a.conflicts(), isEmpty);
    expect(a.get('ups-1')!.name, 'from b');
    expect(File('${a.itemsPath}/ups-1/history/a.jsonl').readAsStringSync(), contains('conflictLoser'));
  });

  test('ids that could escape the folder are refused', () {
    final s = ModelStore(d.path, 'a');
    for (final bad in ['../x', '.hidden', 'A', 'a/b', '', 'x' * 65]) {
      expect(validModelId(bad), isFalse, reason: bad);
      expect(s.get(bad), isNull);
    }
    expect(() => s.put(Item(id: '../x', type: 't', name: 'n')), throwsArgumentError);
  });

  test('a writer with a slow clock still lands after the last version', () {
    final s = ModelStore(d.path, 'a');
    s.put(ups(), now: DateTime.utc(2026, 11, 27, 12));
    final w = ModelStore(d.path, 'b').put(s.get('ups-1')!..name = 'late', now: DateTime.utc(2026, 11, 27, 11));
    expect(w.time!.isAfter(DateTime.utc(2026, 11, 27, 12)), isTrue);
    expect(s.get('ups-1')!.name, 'late');
  });
}
