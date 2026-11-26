import 'package:monitor_core/monitor_core.dart';
import 'package:monitor_core/src/agent/hid_ups.dart';
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
  test('a HID power device descriptor becomes basic UPS status', () {
    // Usage page Power (0x84), collection PowerSummary (0x24); report 1:
    // RemainingCapacity (0x85:0x66) 8 bits, RunTimeToEmpty (0x85:0x68) 16 bits;
    // report 2: ACPresent (0x85:0xD0) 1 bit + 7 bits padding.
    final d = [
      0x05, 0x84, 0x09, 0x24, 0xA1, 0x00, //
      0x85, 0x01, 0x05, 0x85, 0x09, 0x66, 0x15, 0x00, 0x26, 0xFF, 0x00, 0x75, 0x08, 0x95, 0x01, 0xB1, 0x02,
      0x09, 0x68, 0x27, 0xFF, 0xFF, 0x00, 0x00, 0x75, 0x10, 0xB1, 0x02,
      0x85, 0x02, 0x09, 0xD0, 0x25, 0x01, 0x75, 0x01, 0xB1, 0x02, 0x75, 0x07, 0xB1, 0x03,
      0xC0,
    ];
    final f = parseDescriptor(d).where((x) => !x.constant).toList();
    expect(f.map((x) => x.key), ['summary/850066', 'summary/850068', 'summary/8500d0']);
    final rep1 = [100, 0x10, 0x0E]; // 100 %, 3600 s
    final v = {for (final x in f.take(2)) x.key: x.decode(rep1)!, f[2].key: f[2].decode([1])!};
    final r = {for (final x in upsReadings(v)) x.key: x.value};
    expect(r['ups.charge'], 100);
    expect(r['ups.runtime'], 60);
    expect(r['ups.status'], 'mains');
    expect(r['ups.load'], null);
  });
}
