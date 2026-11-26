import 'package:monitor_core/monitor_core.dart';
import 'package:test/test.dart';

Report dev(String id, {List<String> children = const [], String? parent, String kind = 'linux'}) => Report(
    id: id, name: id, kind: kind, time: DateTime.utc(2026, 11, 26),
    node: NodeDecl(id: id, type: kind == 'thing' ? 'ups' : 'device', parent: parent, children: children,
        links: parent == null ? const [] : [NodeLink(to: parent, via: 'usb', dir: 'out')]));

void main() {
  test('a clean hierarchy has no problems', () {
    final d = {'desk': dev('desk', children: ['ups'])};
    final t = {'ups': Thing(dev('ups', parent: 'desk', kind: 'thing'), 'desk')};
    expect(checkTopology(d, t), isEmpty);
  });
  test('broken references are reported', () {
    final d = {'desk': dev('desk')}; // does not list the child
    final t = {'ups': Thing(dev('ups', parent: 'gone', kind: 'thing'), 'desk')};
    expect(checkTopology(d, t), isNotEmpty);
  });
  test('reading value may be null only when unknown', () {
    final r = Report.tryParse({'v': 1, 'id': 'x', 'name': 'x', 'kind': 'thing', 't': 1, 'readings': [
      {'key': 'a', 'value': null, 'src': 'unknown', 'note': 'why'},
      {'key': 'b', 'value': null},
    ]})!;
    expect(r.readings.map((x) => x.key), ['a']);
  });
}
