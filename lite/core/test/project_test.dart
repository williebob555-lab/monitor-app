import 'dart:convert';
import 'dart:io';

import 'package:monitor_core/monitor_core.dart';
import 'package:test/test.dart';

void main() {
  late Directory d;
  setUp(() => d = Directory.systemTemp.createTempSync('project'));
  tearDown(() => d.deleteSync(recursive: true));

  // ChatGPT's example in #81: "Studio rack", 12U, a UPS that is a live thing at U1-U2.
  Project studio() => Project(id: 'studio-rack', name: 'Studio rack', goal: 'Desk-side rack for the desktop and audio', tasks: [
        Task('t1', 'Measure the desk depth'),
      ], assemblies: [
        Assembly(id: 'rack', name: 'Rack', units: 12, depth: Qty(450, 'mm'), maxWeight: Qty(40, 'kg'), placements: [
          Placement(id: 'p1', name: 'UPS', thing: 'usb-0764-0601-port15', u: (1, 2), depth: Qty(14, 'in'), weight: Qty(6, 'kg'), power: Qty(30, 'W')),
          Placement(id: 'p2', name: 'MOTU 828', thing: 'usb-07fd-000e-828e032865', u: (4, 4), depth: Qty(20, 'cm')),
          Placement(id: 'p3', name: 'Patch bay'),
        ]),
      ]);

  test('#81 draft survives a restart and reads the same on another device', () {
    ProjectStore(d.path, 'laptop').put(studio());
    final p = ProjectStore(d.path, 'pixel').get('studio-rack')!;
    expect(p.name, 'Studio rack');
    final a = p.assemblies.single;
    expect(a.units, 12);
    expect(a.placements.first.thing, 'usb-0764-0601-port15');
    expect(a.placements.first.u, (1, 2));
    expect(a.placements.last.u, isNull);
    expect(p.tasks.single.done, isFalse);
  });

  test('a clean layout has no problems; totals say how many parts they cover', () {
    final c = studio().assemblies.single.check();
    expect(c.problems, isEmpty);
    expect(c.weightKg.sum, 6);
    expect(c.weightKg.known, 1);
    expect(c.weightKg.count, 3);
    expect(c.weightKg.complete, isFalse);
    expect(c.freeUnits, 9);
  });

  test('overlap, out of bounds, too deep and over weight are caught', () {
    final a = studio().assemblies.single;
    a.placements.add(Placement(id: 'p4', name: 'Amp', u: (2, 3), weight: Qty(80, 'lb')));
    a.placements.add(Placement(id: 'p5', name: 'Shelf', u: (12, 13)));
    a.placements.add(Placement(id: 'p6', name: 'Server', u: (6, 7), depth: Qty(24, 'in')));
    a.placements.add(Placement(id: 'p7', name: 'Blank', u: (2, 2), face: 'rear'));
    final c = a.check();
    expect(c.problems, contains('UPS and Amp overlap at U2'));
    expect(c.problems.any((p) => p.startsWith('Shelf: U12-U13 is outside')), isTrue);
    expect(c.problems.any((p) => p.startsWith('Server: 609.6 mm deep')), isTrue);
    expect(c.problems.any((p) => p.startsWith('Weight')), isTrue);
    // A rear panel next to front gear is fine.
    expect(c.problems.any((p) => p.contains('Blank')), isFalse);
  });

  test('unknown fields in placements and assemblies survive', () {
    final p = studio();
    p.assemblies.single.placements.first.extra['cableColor'] = 'blue';
    final s = ProjectStore(d.path, 'a')..put(p);
    expect(jsonEncode(s.get('studio-rack')!.toJson()), contains('cableColor'));
  });

  test('archived projects drop out of the list but keep their history', () {
    final s = ProjectStore(d.path, 'a')..put(studio());
    s.archive('studio-rack');
    expect(s.list(), isEmpty);
    expect(s.list(withRetired: true).single.state, 'archived');
    expect(s.history('studio-rack'), hasLength(2));
  });

  test('height is kept for unplaced parts and checked once placed', () {
    final amp = Placement(id: 'a', name: 'Amplifier', height: 2);
    final back = Placement.fromJson(jsonDecode(jsonEncode(amp.toJson())))!;
    expect(back.height, 2);
    expect(back.u, isNull);
    expect(back.units, 2);
    final rack = Assembly(id: 'r', name: 'Rack', units: 12, placements: [back..u = (3, 3)]);
    expect(rack.check().problems, contains('Amplifier is 2U but placed at U3-U3'));
    back.u = (3, 4);
    expect(rack.check().problems, isEmpty);
  });
}
