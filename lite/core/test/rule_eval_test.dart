import 'dart:io';

import 'package:monitor_core/monitor_core.dart';
import 'package:test/test.dart';

void main() {
  late Directory dir;
  late String root;
  setUp(() {
    dir = Directory.systemTemp.createTempSync('ruleeval');
    root = '${dir.path}/Monitor';
    Directory('$root/devices').createSync(recursive: true);
  });
  tearDown(() => dir.deleteSync(recursive: true));

  final t0 = DateTime.utc(2026, 11, 27, 14);
  Report rep(String id, String kind, DateTime t, {String app = '2.6.9', String? machine, Map<String, Object?> extra = const {}}) => Report.tryParse({
        'v': 2, 'id': id, 'name': id, 'kind': kind, 't': t.millisecondsSinceEpoch ~/ 1000, 'app': app, 'next': 60,
        if (machine != null) 'machine': machine, ...extra,
      })!;
  Rule rule(Map<String, Object?> j) => Rule.parse({'id': 'r', 'then': [{'type': 'notify'}, {'type': 'annotate'}], ...j}).$1!;
  RuleSet set(Rule r) => RuleSet([r], {});

  test('the evaluator is the lowest fresh computer on a new enough version', () {
    final now = t0;
    expect(ruleEvaluator({
      'b': rep('b', 'linux', now),
      'a': rep('a', 'windows', now, app: '2.6.8'),
      'c': rep('c', 'linux', now.subtract(const Duration(hours: 1))),
      '0': rep('0', 'android', now),
    }, now), 'b');
  });

  test('"laptop offline 10 min" fires exactly once, with the reason, across two evaluators', () {
    final r = rule({'id': 'laptop-offline', 'name': 'Laptop offline', 'when': {'type': 'offline', 'for': 600}, 'scope': {'devices': ['laptop']}});
    final a = RuleEngine('${dir.path}/a.json'), b = RuleEngine('${dir.path}/b.json');
    final fired = <Firing>[];
    final laptopLast = t0;
    for (var m = 1; m <= 40; m++) {
      final now = t0.add(Duration(minutes: m));
      final reports = {'laptop': rep('laptop', 'windows', laptopLast), 'a': rep('a', 'linux', now), 'b': rep('b', 'linux', now)};
      fired.addAll(a.evaluate(root: root, me: 'a', rules: set(r), reports: reports, now: now));
      fired.addAll(b.evaluate(root: root, me: 'b', rules: set(r), reports: reports, now: now));
    }
    expect(fired, hasLength(1));
    expect(fired.single.why, 'laptop: no report for 10 min (last 14:00Z)');
    expect(fired.single.time, t0.add(const Duration(minutes: 10)));
    final lines = firedLines(root, 'laptop-offline');
    expect(lines.single['by'], 'a');
    expect(lines.single['did'], ['notify', 'annotate']);
    expect(readEvents(root, t0, t0.add(const Duration(hours: 1))).single.type, 'rule.fired');

    // Evaluator a goes away mid-episode; b takes over and does not repeat it.
    final now = t0.add(const Duration(minutes: 50));
    final reports = {'laptop': rep('laptop', 'windows', laptopLast), 'b': rep('b', 'linux', now)};
    expect(RuleEngine('${dir.path}/fresh.json').evaluate(root: root, me: 'b', rules: set(r), reports: reports, now: now), isEmpty);
  });

  test('offline does not fire while the dual-boot twin is up', () {
    final r = rule({'when': {'type': 'offline'}});
    final e = RuleEngine('${dir.path}/s.json');
    for (var m = 0; m <= 20; m++) {
      final now = t0.add(Duration(minutes: m));
      final reports = {'desk': rep('desk', 'linux', t0, machine: 'pc'), 'alpha': rep('alpha', 'linux', now, machine: 'pc')};
      expect(e.evaluate(root: root, me: 'alpha', rules: set(r), reports: reports, now: now), isEmpty);
    }
  });

  test('value with "for" fires once per episode; a computer judges itself', () {
    final r = rule({'when': {'type': 'value', 'path': 'readings.ups.charge', 'op': '<', 'value': 50, 'for': 120}, 'cooldown': 0});
    final e = RuleEngine('${dir.path}/v.json');
    final out = <Firing>[];
    for (var m = 0; m < 12; m++) {
      final now = t0.add(Duration(minutes: m));
      final charge = m < 2 || (m >= 6 && m < 8) ? 100 : 40; // two episodes
      final self = rep('pc', 'linux', now, extra: {'readings': [{'key': 'ups.charge', 'value': charge}]});
      out.addAll(e.evaluate(root: root, me: 'pc', rules: set(r), reports: {'pc': self}, now: now));
    }
    expect(out.map((f) => f.time.minute), [4, 10]);
    expect(out.first.why, 'pc: readings.ups.charge is 40 (< 50)');
  });

  test('cooldown keeps a second episode quiet; unknown values never fire', () {
    final r = rule({'when': {'type': 'value', 'path': 'battery.pct', 'op': '<', 'value': 20}, 'cooldown': 3600});
    final e = RuleEngine('${dir.path}/c.json');
    var n = 0;
    for (var m = 0; m < 10; m++) {
      final now = t0.add(Duration(minutes: m));
      final pct = m.isEven ? 10 : 90;
      n += e.evaluate(root: root, me: 'pc', rules: set(r), reports: {'pc': rep('pc', 'linux', now, extra: {'battery': {'pct': pct}})}, now: now).length;
    }
    expect(n, 1);
    expect(e.evaluate(root: root, me: 'pc', rules: set(rule({'id': 'u', 'when': {'type': 'value', 'path': 'nope', 'op': '!=', 'value': 1}})), reports: {'pc': rep('pc', 'linux', t0)}, now: t0), isEmpty);
  });

  test('events: a matching event fires; old ones and other devices\' do not', () {
    final r = rule({'when': {'type': 'event', 'event': 'container.*'}, 'cooldown': 0});
    final e = RuleEngine('${dir.path}/e.json');
    appendEvents('$root/devices/pc', [Event(t0.subtract(const Duration(hours: 1)), 'container.stopped', subject: 'ctr-old')]);
    final reports = {'pc': rep('pc', 'linux', t0), 'other': rep('other', 'linux', t0)};
    expect(e.evaluate(root: root, me: 'pc', rules: set(r), reports: reports, now: t0), isEmpty);
    final t1 = t0.add(const Duration(minutes: 1));
    appendEvents('$root/devices/pc', [Event(t1.subtract(const Duration(seconds: 5)), 'container.stopped', subject: 'ctr-web', name: 'web')]);
    appendEvents('$root/devices/other', [Event(t1.subtract(const Duration(seconds: 5)), 'container.stopped', subject: 'ctr-x')]);
    final f = e.evaluate(root: root, me: 'pc', rules: set(r), reports: reports, now: t1);
    expect(f.single.subject, 'ctr-web');
    expect(f.single.why, 'pc: container.stopped web');
  });

  test('a control action sends a normal request from the evaluator', () {
    final r = rule({'when': {'type': 'unusual'}, 'then': [{'type': 'control', 'host': 'pc', 'target': 'ups', 'key': 'ups.alarm', 'value': 'off'}]});
    final self = rep('pc', 'linux', t0, extra: {'readings': [{'key': 'unusual.cpu', 'value': 'CPU 70 %, usually 8 % at night'}]});
    final f = RuleEngine('${dir.path}/k.json').evaluate(root: root, me: 'pc', rules: set(r), reports: {'pc': self}, now: t0);
    expect(f.single.did.single, startsWith('control ups.alarm=off sent to pc'));
    expect(SyncFolder(root).requestsFor('pc', t0).single.key, 'ups.alarm');
  });
}
