import 'package:monitor_core/monitor_core.dart';
import 'package:test/test.dart';

final now = DateTime.utc(2026, 11, 26, 12);

Report r(String id, {int ageS = 30, String kind = 'linux', String? machine, Map<String, PeerView>? peers, Battery? battery, int next = 60}) =>
    Report(
      id: id,
      name: id,
      kind: kind,
      time: now.subtract(Duration(seconds: ageS)),
      machine: machine ?? 'm-$id',
      tailscaleIp: '100.64.0.${id.length}',
      syncthingLocal: true,
      nextSeconds: next,
      battery: battery,
      peers: peers ?? {},
    );

PeerView ok([int? seen = 20]) => PeerView(tcp: 'open', pingMs: 12, seenAge: seen);
PeerView refused([int? seen]) => PeerView(tcp: 'refused', pingMs: 10, seenAge: seen);
PeerView gone([int? seen]) => PeerView(tcp: 'timeout', seenAge: seen);

Map<String, Report> m(List<Report> l) => {for (final x in l) x.id: x};
DeviceStatus of(Assessment a, String id) => a.devices.firstWhere((d) => d.report.id == id);

void main() {
  test('everyone fresh and reachable', () {
    final a = assess(m([
      r('laptop', peers: {'pixel': ok(), 'tablet': ok()}),
      r('pixel', kind: 'android', peers: {'laptop': ok(), 'tablet': ok()}),
      r('tablet', kind: 'android', peers: {'laptop': ok(), 'pixel': ok()}),
    ]), myId: 'pixel', now: now);
    expect(a.devices.map((d) => d.level).toSet(), {Level.ok});
    expect(a.network.title, 'All 3 devices online');
    expect(a.devices.first.report.id, 'pixel');
  });

  test('reachable but refusing the Syncthing port = Syncthing stopped', () {
    final a = assess(m([
      r('laptop', peers: {'tablet': refused(900)}),
      r('pixel', kind: 'android', peers: {'tablet': refused(900)}),
      r('tablet', ageS: 900),
    ]), myId: 'laptop', now: now);
    expect(of(a, 'tablet').title, 'Syncthing stopped');
    expect(of(a, 'tablet').level, Level.fault);
  });

  test('android reachable, Syncthing listening, but files stale', () {
    final a = assess(m([
      r('laptop', peers: {'pixel': ok(900)}),
      r('pixel', kind: 'android', ageS: 900),
    ]), myId: 'laptop', now: now);
    expect(of(a, 'pixel').title, 'Reachable, not syncing');
    expect(of(a, 'pixel').detail, contains('Syncthing-Fork'));
  });

  test('nobody can reach it and files stale = offline; low battery is named', () {
    final a = assess(m([
      r('laptop', peers: {'pixel': ok(), 'tablet': gone(3000)}),
      r('pixel', kind: 'android', peers: {'laptop': ok(), 'tablet': gone(3000)}),
      r('tablet', kind: 'android', ageS: 3000, battery: Battery(pct: 4, charging: false)),
    ]), myId: 'laptop', now: now);
    expect(of(a, 'tablet').title, 'Offline, battery flat?');
    expect(a.network.title, '2 of 3 devices online');
  });

  test('fresh files but no Tailscale answer = Tailscale unreachable', () {
    final a = assess(m([
      r('laptop', peers: {'pixel': gone(20)}),
      r('pixel', kind: 'android', peers: {}),
    ]), myId: 'laptop', now: now);
    expect(of(a, 'pixel').title, 'Tailscale unreachable');
  });

  test('two groups that cannot reach each other = network split', () {
    final a = assess(m([
      r('laptop', peers: {'pixel': ok(), 'tablet': gone()}),
      r('pixel', kind: 'android', peers: {'laptop': ok(), 'tablet': gone()}),
      r('tablet', kind: 'android', peers: {'laptop': gone(), 'pixel': gone()}),
    ]), myId: 'laptop', now: now);
    expect(a.network.title, 'Network split');
    expect(a.network.groups, containsAll([
      ['laptop', 'pixel'],
      ['tablet'],
    ]));
  });

  test('dual boot: the OS not running is gray, never red', () {
    final a = assess(m([
      r('laptop-win', ageS: 20000, machine: 'hw1'),
      r('laptop-fedora', machine: 'hw1', peers: {'pixel': ok()}),
      r('pixel', kind: 'android', peers: {'laptop-fedora': ok(), 'laptop-win': gone()}),
    ]), myId: 'pixel', now: now);
    expect(of(a, 'laptop-win').level, Level.inactive);
    expect(a.network.level, Level.ok);
    expect(a.network.title, 'Both devices online');
  });

  test('this device stopped writing = not collecting', () {
    final a = assess(m([r('pixel', kind: 'android', ageS: 2000, next: 300)]), myId: 'pixel', now: now);
    expect(of(a, 'pixel').title, 'Not collecting');
    expect(of(a, 'pixel').detail, contains('battery'));
  });

  test('a phone writing every 5 minutes with the screen off is still fresh at 10 minutes', () {
    final a = assess(m([r('pixel', kind: 'android', ageS: 600, next: 300), r('laptop', peers: {'pixel': ok(600)})]),
        myId: 'laptop', now: now);
    expect(of(a, 'pixel').level, Level.ok);
  });

  test('report round-trips through JSON', () {
    final x = r('laptop', peers: {'pixel': ok()}, battery: Battery(pct: 55, charging: true));
    final y = Report.tryParse(x.toJson())!;
    expect(y.peers['pixel']!.tcp, 'open');
    expect(y.battery!.pct, 55);
    expect(y.time, x.time.subtract(Duration(microseconds: x.time.microsecond)));
  });

  test('extra readings round-trip and bad ones are dropped', () {
    final y = Report.tryParse({
      'id': 'a',
      't': 1,
      'readings': [
        {'key': 'temp.cpu', 'label': 'CPU temperature', 'value': 54.2, 'unit': '°C', 'group': 'Sensors'},
        {'key': 'svc.syncthing', 'value': true},
        {'value': 3},
        'junk',
      ],
    })!;
    expect(y.readings.map((r) => r.key), ['temp.cpu', 'svc.syncthing']);
    expect(Report.tryParse(y.toJson())!.readings.first.unit, '°C');
  });

  test('garbage files are ignored, not fatal', () {
    expect(Report.tryParse('nope'), isNull);
    expect(Report.tryParse({'id': 3}), isNull);
    expect(Report.tryParse({'id': 'a', 't': 1, 'peers': 'x', 'mem': 5})!.id, 'a');
  });

  test('versions compare numerically', () {
    expect(compareVersions('2.0.10', '2.0.9'), greaterThan(0));
    expect(compareVersions('dev', '2.0.0'), lessThan(0));
  });
}
