import 'dart:convert';
import 'dart:io';

import 'package:monitor_core/monitor_core.dart';
import 'package:test/test.dart';

void main() {
  late Directory tmp;
  setUp(() => tmp = Directory.systemTemp.createTempSync('past'));
  tearDown(() => tmp.deleteSync(recursive: true));
  int s(DateTime t) => t.millisecondsSinceEpoch ~/ 1000;

  test('yesterday shows yesterday\'s design and who was up, joined through observes', () {
    final t0 = DateTime.utc(2026, 11, 26, 12), t1 = DateTime.utc(2026, 11, 27, 12);
    final store = ModelStore(tmp.path, 'pc');
    store.put(Item(id: 'nas', type: 'computer', name: 'Old NAS', state: 'owned', observes: 'srv'), now: t0.subtract(const Duration(hours: 1)));
    store.put(Item(id: 'nas', type: 'computer', name: 'NAS (renamed)', state: 'owned', observes: 'srv'), now: t1);
    store.put(Item(id: 'ups', type: 'ups', name: 'UPS', state: 'owned', observes: 'usb-0764-0601-x'), now: t1);
    ProjectStore(tmp.path, 'pc').put(Project(id: 'rack', name: 'Rack v1'), now: t0.subtract(const Duration(minutes: 5)));
    final f = File('${tmp.path}/devices/srv/history/detail/${detailName(t0)}')..createSync(recursive: true);
    f.writeAsStringSync([for (var m = -30; m < 30; m++) '${jsonEncode({'t': s(t0.add(Duration(minutes: m)))})}\n'].join());

    final y = systemAt(tmp.path, t0, now: t1);
    expect(y.items.single.name, 'Old NAS');
    expect(y.projects.single.name, 'Rack v1');
    expect(y.upAt(y.items.single), isTrue);
    final later = systemAt(tmp.path, t0.add(const Duration(hours: 2)), now: t1);
    expect(later.upAt(later.items.single), isFalse);
    expect(systemAt(tmp.path, t1, now: t1).items.map((i) => i.name), unorderedEquals(['NAS (renamed)', 'UPS']));
  });
}
