import 'dart:convert';
import 'dart:io';

import 'control.dart';
import 'model.dart' show validModelId;

/// Rules (roadmap row 18): `rules/<id>.json` in the shared folder, one file
/// per rule, so devices never edit the same file. FORMAT.md "Rules".
///
/// ```json
/// {"v": 1, "id": "laptop-offline", "name": "Laptop offline 10 min",
///  "enabled": true,
///  "when": {"type": "offline", "for": 600},
///  "scope": {"devices": ["301230114386"]},
///  "then": [{"type": "notify"}, {"type": "annotate"}],
///  "cooldown": 3600}
/// ```
///
/// Triggers (`when.type`):
/// * `value`: `path` (a `Report.field` path such as `battery.pct` or
///   `readings.ups.charge`), `op` (`<` `<=` `>` `>=` `==` `!=`), `value`
///   (number or text), optional `for` (seconds it must hold).
/// * `event`: `event` (an events type such as `peer.offline`, or a prefix
///   ending in `*`, like `container.*`).
/// * `unusual`: any `unusual.*` reading appears.
/// * `offline`: the device's reports stop for `for` seconds (default 600).
///
/// Scope (all lists optional; empty or missing = everything):
/// `devices` (ids), `things` (ids), `types` (`android`, `windows`,
/// `linux`, or `thing:<kind>`, e.g. `thing:ups`, `thing:container`).
///
/// Actions (`then`): `notify` (optional `text`), `annotate` (a note on the
/// history at that time), `control` (`host`, `target`, `key`, `value`, the
/// same request another device could send; subject to `controls.allow`).
class Rule {
  Rule({required this.id, required this.name, required this.when, this.scope = const {}, required this.then, this.cooldown = 3600, this.enabled = true, Map<String, Object?>? extra})
      : extra = extra ?? {};
  final String id, name;
  final Map<String, Object?> when;
  final Map<String, List<String>> scope;
  final List<Map<String, Object?>> then;

  /// Seconds before the same rule may fire again for the same subject.
  final int cooldown;
  final bool enabled;
  final Map<String, Object?> extra;

  String get trigger => '${when['type']}';

  static const _known = {'v', 'id', 'name', 'enabled', 'when', 'scope', 'then', 'cooldown'};

  /// Parses and validates; returns the rule and the problems in words.
  /// A rule with problems is null and never runs.
  static (Rule?, List<String>) parse(Object? j) {
    final p = validateRule(j);
    if (p.isNotEmpty) return (null, p);
    final m = j as Map;
    final scope = <String, List<String>>{};
    if (m['scope'] is Map) {
      (m['scope'] as Map).forEach((k, v) {
        if (v is List) scope['$k'] = [for (final x in v) '$x'];
      });
    }
    return (
      Rule(
        id: m['id'] as String,
        name: '${m['name'] ?? m['id']}',
        when: Map<String, Object?>.from(m['when'] as Map),
        scope: scope,
        then: [for (final a in m['then'] as List) Map<String, Object?>.from(a as Map)],
        cooldown: (m['cooldown'] as num?)?.toInt() ?? 3600,
        enabled: m['enabled'] != false,
        extra: {for (final e in m.entries) if (e.key is String && !_known.contains(e.key)) e.key as String: e.value},
      ),
      const <String>[]
    );
  }

  Map<String, Object?> toJson() => {
        ...extra,
        'v': 1,
        'id': id,
        'name': name,
        'enabled': enabled,
        'when': when,
        if (scope.isNotEmpty) 'scope': scope,
        'then': then,
        'cooldown': cooldown,
      };

  /// Whether [deviceOrThing] (with its [type], e.g. `android` or
  /// `thing:ups`) is in scope.
  bool covers(String id, String type, {bool isThing = false}) {
    final d = scope['devices'] ?? const [], t = scope['things'] ?? const [], ty = scope['types'] ?? const [];
    if (d.isEmpty && t.isEmpty && ty.isEmpty) return true;
    if (ty.contains(type)) return true;
    return isThing ? t.contains(id) : d.contains(id);
  }
}

const _ops = {'<', '<=', '>', '>=', '==', '!='};
const _triggers = {'value', 'event', 'unusual', 'offline'};
const _actions = {'notify', 'annotate', 'control'};
const _scopeKeys = {'devices', 'things', 'types'};

/// Problems with a rule file, as sentences; empty when it is valid.
List<String> validateRule(Object? j) {
  if (j is! Map) return ['a rule is a JSON object'];
  final p = <String>[];
  final id = j['id'];
  if (id is! String || !validModelId(id)) p.add('id must be lower-case letters, digits, "-", "_" or "." (at most 64)');
  if (j['name'] != null && (j['name'] is! String || (j['name'] as String).length > 120)) p.add('name must be text of at most 120 characters');
  if (j['enabled'] != null && j['enabled'] is! bool) p.add('enabled must be true or false');
  final cd = j['cooldown'];
  if (cd != null && (cd is! num || cd < 0 || cd > 30 * 86400)) p.add('cooldown must be seconds between 0 and 30 days');

  final w = j['when'];
  if (w is! Map) {
    p.add('"when" is missing');
  } else {
    final type = w['type'];
    if (!_triggers.contains(type)) {
      p.add('"when.type" must be one of ${_triggers.join(', ')}');
    } else {
      final f = w['for'];
      if (f != null && (f is! num || f < 0 || f > 7 * 86400)) p.add('"when.for" must be seconds between 0 and 7 days');
      switch (type) {
        case 'value':
          if (w['path'] is! String || (w['path'] as String).isEmpty || (w['path'] as String).length > 200) p.add('"when.path" names the value, e.g. battery.pct');
          if (!_ops.contains(w['op'])) p.add('"when.op" must be one of ${_ops.join(' ')}');
          final v = w['value'];
          if (v is! num && v is! String && v is! bool) p.add('"when.value" must be a number, text or true/false');
          if (v is! num && const {'<', '<=', '>', '>='}.contains(w['op'])) p.add('"when.op" ${w['op']} needs a number');
        case 'event':
          final e = w['event'];
          if (e is! String || !RegExp(r'^[a-z0-9._-]+\*?$').hasMatch(e)) p.add('"when.event" names an event type, e.g. peer.offline or container.*');
      }
    }
  }

  final s = j['scope'];
  if (s != null) {
    if (s is! Map) {
      p.add('"scope" must be an object');
    } else {
      for (final e in s.entries) {
        if (!_scopeKeys.contains(e.key)) p.add('"scope.${e.key}" is not known (devices, things, types)');
        if (e.value is! List || (e.value as List).any((x) => x is! String || !ControlRequest.validName(x.replaceFirst('thing:', '')))) {
          p.add('"scope.${e.key}" must be a list of ids');
        }
      }
    }
  }

  final t = j['then'];
  if (t is! List || t.isEmpty) {
    p.add('"then" must list at least one action');
  } else {
    for (final a in t) {
      if (a is! Map || !_actions.contains(a['type'])) {
        p.add('each action has a type: ${_actions.join(', ')}');
        continue;
      }
      if (a['text'] != null && (a['text'] is! String || (a['text'] as String).length > 500)) p.add('action text must be at most 500 characters');
      if (a['type'] == 'control') {
        for (final k in const ['host', 'target']) {
          if (a[k] is! String || !ControlRequest.validName(a[k] as String)) p.add('a control action needs "$k", a device or thing id');
        }
        if (a['key'] is! String || !RegExp(r'^[a-z0-9._-]{1,64}$').hasMatch(a['key'] as String)) p.add('a control action needs "key", e.g. ups.alarm');
        if (a['value'] is! String || (a['value'] as String).length > 256) p.add('a control action needs "value" as text');
      }
    }
  }
  return p;
}

/// The rules in `<folder>/rules/`. Unreadable or invalid files are listed
/// in [problems] by file name and never run.
class RuleSet {
  RuleSet(this.rules, this.problems);
  final List<Rule> rules;
  final Map<String, List<String>> problems;

  static String dir(String folder) => '$folder${Platform.pathSeparator}rules';

  static RuleSet load(String folder) {
    final d = Directory(dir(folder));
    final rules = <Rule>[];
    final problems = <String, List<String>>{};
    if (!d.existsSync()) return RuleSet(rules, problems);
    for (final f in d.listSync().whereType<File>()) {
      final name = f.uri.pathSegments.last;
      if (!name.endsWith('.json') || name.startsWith('.') || name.contains('.sync-conflict-')) continue;
      try {
        if (f.lengthSync() > 64 * 1024) {
          problems[name] = ['the file is over 64 KB'];
          continue;
        }
        final (r, p) = Rule.parse(jsonDecode(f.readAsStringSync()));
        if (r == null) {
          problems[name] = p;
        } else if ('${r.id}.json' != name) {
          problems[name] = ['its id "${r.id}" does not match the file name'];
        } else {
          rules.add(r);
        }
      } catch (e) {
        problems[name] = ['not valid JSON'];
      }
    }
    rules.sort((a, b) => a.id.compareTo(b.id));
    return RuleSet(rules, problems);
  }

  /// Writes [r] atomically as rules/<id>.json.
  static void save(String folder, Rule r) {
    final p = validateRule(r.toJson());
    if (p.isNotEmpty) throw ArgumentError(p.join('; '));
    final d = Directory(dir(folder))..createSync(recursive: true);
    final sep = Platform.pathSeparator;
    final tmp = File('${d.path}$sep.syncthing.${r.id}.json.tmp');
    tmp.writeAsStringSync(const JsonEncoder.withIndent('  ').convert(r.toJson()), flush: true);
    tmp.renameSync('${d.path}$sep${r.id}.json');
  }
}
