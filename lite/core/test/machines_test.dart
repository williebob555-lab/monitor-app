import 'package:monitor_core/monitor_core.dart';
import 'package:test/test.dart';

// Shaped like wc-alpha / wc-desktop on 2026-11-27: one Ryzen box, two
// systems, alpha running and desktop last heard from 2.4 h earlier.
Report r(String id, String name, int t, {String? machine, String kind = 'linux', String? os}) =>
    Report.tryParse({'v': 1, 'id': id, 'name': name, 'kind': kind, 't': t, 'next': 60, 'machine': machine, 'os': os})!;

void main() {
  final now = DateTime.fromMillisecondsSinceEpoch(1795812480 * 1000, isUtc: true);
  final reports = {
    'd8fea3c0a082': r('d8fea3c0a082', 'wc-alpha', 1795812469, machine: '81d1fe609ef2e207', os: 'Ubuntu 26.10'),
    'fa2c89ad107d': r('fa2c89ad107d', 'wc-desktop', 1795803901, machine: '81d1fe609ef2e207', os: 'Fedora Linux 45'),
    'ebad35097c54': r('ebad35097c54', 'wc-beta', 1795812411, machine: 'd195b5b33a4433c4', os: 'Ubuntu 26.04.1 LTS'),
    'e1b8ee956de3': r('e1b8ee956de3', 'pixel', 1795812400, kind: 'android', machine: 'x'),
    '13e15a76088f': r('13e15a76088f', 'wc-tab', 1795812400, kind: 'android', machine: 'x'),
  };

  test('a dual-boot machine is one machine, running system first', () {
    final a = assess(reports, myId: 'ebad35097c54', now: now);
    expect(a.devices, hasLength(5));
    expect(a.machines, hasLength(4));
    final box = a.machines.singleWhere((m) => m.systems.length == 2);
    expect(box.current.report.name, 'wc-alpha');
    expect(box.others.single.report.name, 'wc-desktop');
    expect(box.others.single.title, 'Not booted');
    expect(box.name, 'wc-alpha / wc-desktop');
    expect(a.systemsOf('fa2c89ad107d'), ['fa2c89ad107d', 'd8fea3c0a082']);
  });

  test('phones never group, even with the same key', () {
    final a = assess(reports, now: now);
    expect(a.machines.where((m) => m.current.report.kind == 'android'), hasLength(2));
  });

  test('the system not booted is not counted as away', () {
    final a = assess(reports, now: now);
    expect('${a.network.title} ${a.network.detail}', isNot(contains('away')));
  });

  // laptop (Windows) and laptop-f (Fedora) on 2.6.0: keys derived
  // differently, so only the hardware readings tie them.
  Report lap(String id, String name, String kind, int t, String key, String model, String app) => Report.tryParse({
        'v': 1, 'id': id, 'name': name, 'kind': kind, 't': t, 'next': 60, 'machine': key, 'os': kind, 'app': app,
        'readings': [
          {'key': 'sys.maker', 'value': 'HP'},
          {'key': 'sys.model', 'value': model},
          {'key': 'sys.cpu', 'value': 'Intel(R) Core(TM) Ultra 7 256V'},
        ],
      })!;

  test('laptop twins on 2.6.0 are one machine; Fedora shows Not booted', () {
    final rs = {
      '301230114386': lap('301230114386', 'Wilsons-Laptop', 'windows', 1795812478, 'd02e33b8b7c0432a', 'HP OmniBook Ultra Flip Laptop 14-fh0xxx', '2.6.0'),
      'eaf9a8030291': lap('eaf9a8030291', 'wc-laptop-f', 'linux', 1795800000, '8b44d33523e2f72a', 'HP OmniBook Ultra Flip Laptop 14-fh0xxx (SBKPF)', '2.6.0'),
    };
    final a = assess(rs, now: now);
    expect(a.machines, hasLength(1));
    expect(a.machines.single.name, 'Wilsons-Laptop / wc-laptop-f');
    expect(a.devices.singleWhere((d) => d.report.id == 'eaf9a8030291').title, 'Not booted');
  });

  test('on 2.6.1+ different keys mean different machines', () {
    final rs = {
      'a': lap('a', 'one', 'windows', 1795812478, 'k1', 'HP OmniBook', '2.7.1'),
      'b': lap('b', 'two', 'linux', 1795812470, 'k2', 'HP OmniBook', '2.7.1'),
    };
    expect(assess(rs, now: now).machines, hasLength(2));
  });
}
