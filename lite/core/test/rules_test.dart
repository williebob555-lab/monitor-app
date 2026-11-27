import 'dart:convert';
import 'dart:io';

import 'package:monitor_core/monitor_core.dart';
import 'package:test/test.dart';

void main() {
  final good = {
    'v': 1,
    'id': 'laptop-offline',
    'name': 'Laptop offline 10 min',
    'when': {'type': 'offline', 'for': 600},
    'scope': {'devices': ['301230114386']},
    'then': [{'type': 'notify'}, {'type': 'annotate'}],
    'cooldown': 3600,
    'colour': 'red',
  };

  test('a valid rule parses and round-trips, keeping unknown keys', () {
    final (r, p) = Rule.parse(good);
    expect(p, isEmpty);
    expect(r!.trigger, 'offline');
    expect(r.enabled, isTrue);
    expect(r.toJson()['colour'], 'red');
    final (again, p2) = Rule.parse(jsonDecode(jsonEncode(r.toJson())));
    expect(p2, isEmpty);
    expect(again!.scope['devices'], ['301230114386']);
  });

  test('problems come back as sentences and the rule never runs', () {
    final (r, p) = Rule.parse({
      'id': '../x',
      'when': {'type': 'value', 'path': 'battery.pct', 'op': '<', 'value': 'low'},
      'scope': {'devices': ['a/b'], 'rooms': []},
      'then': [{'type': 'control', 'host': 'x'}, {'type': 'shell'}],
    });
    expect(r, isNull);
    expect(p, contains(startsWith('id must be')));
    expect(p, contains('"when.op" < needs a number'));
    expect(p, contains('"scope.devices" must be a list of ids'));
    expect(p, contains(startsWith('"scope.rooms" is not known')));
    expect(p, contains('a control action needs "target", a device or thing id'));
    expect(p, contains(startsWith('each action has a type')));
    expect(validateRule({'id': 'a', 'when': {'type': 'event', 'event': 'container.*'}, 'then': []}), ['"then" must list at least one action']);
  });

  test('scope: empty covers everything; types, devices and things match', () {
    final all = Rule(id: 'a', name: 'a', when: {'type': 'unusual'}, then: [{'type': 'notify'}]);
    expect(all.covers('x', 'android'), isTrue);
    final r = Rule(id: 'b', name: 'b', when: {'type': 'unusual'}, then: [{'type': 'notify'}], scope: {
      'devices': ['d1'],
      'things': ['ups'],
      'types': ['thing:container'],
    });
    expect(r.covers('d1', 'linux'), isTrue);
    expect(r.covers('d2', 'linux'), isFalse);
    expect(r.covers('ups', 'thing:ups', isThing: true), isTrue);
    expect(r.covers('ctr-web', 'thing:container', isThing: true), isTrue);
    expect(r.covers('d1', 'thing:x', isThing: true), isFalse);
  });

  test('load lists bad files by name; save writes rules/<id>.json', () {
    final dir = Directory.systemTemp.createTempSync('rules');
    addTearDown(() => dir.deleteSync(recursive: true));
    final (r, _) = Rule.parse(good);
    RuleSet.save(dir.path, r!);
    final rd = RuleSet.dir(dir.path);
    File('$rd/other.json').writeAsStringSync(jsonEncode({...good, 'id': 'not-other'}));
    File('$rd/broken.json').writeAsStringSync('{');
    File('$rd/x.sync-conflict-1.json').writeAsStringSync('{');
    final s = RuleSet.load(dir.path);
    expect(s.rules.map((r) => r.id), ['laptop-offline']);
    expect(s.problems['other.json'], ['its id "not-other" does not match the file name']);
    expect(s.problems['broken.json'], ['not valid JSON']);
    expect(s.problems.length, 2);
  });

  test('defaults: valid, written once, a changed or deleted one stays as the user left it', () {
    for (final j in defaultRules) {
      expect(validateRule(j), isEmpty, reason: '${j['id']}');
    }
    final dir = Directory.systemTemp.createTempSync('defaults');
    addTearDown(() => dir.deleteSync(recursive: true));
    expect(ensureDefaultRules(dir.path), hasLength(defaultRules.length));
    final s = RuleSet.load(dir.path);
    expect(s.problems, isEmpty);
    expect(s.rules.every((r) => !r.enabled), isTrue);
    File('${RuleSet.dir(dir.path)}/disk-over-90.json').deleteSync();
    final (on, _) = Rule.parse({...defaultRules.first, 'enabled': true});
    RuleSet.save(dir.path, on!);
    expect(ensureDefaultRules(dir.path), isEmpty);
    final again = RuleSet.load(dir.path);
    expect(again.rules.map((r) => r.id), isNot(contains('disk-over-90')));
    expect(again.rules.firstWhere((r) => r.id == defaultRules.first['id']).enabled, isTrue);
  });
}
