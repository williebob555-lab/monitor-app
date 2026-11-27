import 'dart:convert';
import 'dart:io';

import 'control.dart';
import 'events.dart';
import 'folder.dart';
import 'history.dart' show readLines;
import 'report.dart';
import 'rules.dart';
import 'status.dart' show freshLimit, sameMachine;
import 'version.dart';

/// Rule evaluation (roadmap row 19). FORMAT.md "Rules".
///
/// Who evaluates what, so each rule fires once:
/// * a computer evaluates value, event and unusual rules about itself and
///   the things it hosts;
/// * everything else (offline rules, and rules about phones, which run no
///   Dart agent) is evaluated by one computer: the lowest id among the
///   fresh computers on [rulesSince] or newer. Every computer works this out
///   from the same files, so they agree.
///
/// A condition fires once per episode: when it has held for `for` seconds,
/// then not again until it has stopped holding. `cooldown` also spaces
/// episodes out. Each firing is one line in
/// `rules/<id>/fired/<evaluator>.jsonl`; a firing whose episode (`since`)
/// is already there, from any evaluator, is not repeated.
const rulesSince = '2.6.9';

/// One firing: what happened and why.
class Firing {
  Firing({required this.rule, required this.subject, required this.since, required this.time, required this.why, required this.did, this.notify, this.notifyOn});
  final Rule rule;
  final String subject;
  final DateTime since, time;
  final String why;
  final List<String> did;

  /// The notification text when the rule has a notify action (row 20 shows
  /// it on every device).
  final String? notify;

  /// Devices that show it; null means every device.
  final List<String>? notifyOn;

  Map<String, Object?> toJson(String by) => {
        't': time.millisecondsSinceEpoch ~/ 1000,
        'rule': rule.id,
        'subject': subject,
        'since': since.millisecondsSinceEpoch ~/ 1000,
        'why': why,
        'did': did,
        'name': rule.name,
        if (notify != null) 'notify': notify,
        if (notifyOn != null) 'on': notifyOn,
        'by': by,
      };
}

bool _canEvaluate(Report r) => r.kind != 'android' && !r.isThing && r.app != null && (r.app == 'dev' || compareVersions(r.app!, rulesSince) >= 0);

/// The computer that evaluates rules about others, or null when none is
/// fresh. [reports] holds every device, this one included.
String? ruleEvaluator(Map<String, Report> reports, DateTime now) {
  final ids = [
    for (final r in reports.values)
      if (_canEvaluate(r) && now.difference(r.time) <= freshLimit(r)) r.id
  ]..sort();
  return ids.firstOrNull;
}

String _typeOf(Report r) => r.isThing ? 'thing:${r.node?.type ?? 'thing'}' : r.kind;

String _age(Duration d) => d.inMinutes >= 120 ? '${d.inHours} h' : (d.inSeconds >= 120 ? '${d.inMinutes} min' : '${d.inSeconds} s');

String _hm(DateTime t) => '${t.toUtc().toIso8601String().substring(11, 16)}Z';

/// Evaluates [RuleSet]s for one device ([me]) and keeps the episode state in
/// a small local file ([statePath]) outside the shared folder.
class RuleEngine {
  RuleEngine(this.statePath) {
    try {
      final j = jsonDecode(File(statePath).readAsStringSync()) as Map;
      (j['since'] as Map? ?? {}).forEach((k, v) => _since['$k'] = (v as num).toInt());
      (j['done'] as List? ?? []).forEach((k) => _done.add('$k'));
      if (j['ev'] is num) _evAt = DateTime.fromMillisecondsSinceEpoch((j['ev'] as num).toInt() * 1000, isUtc: true);
    } catch (_) {}
  }

  final String statePath;

  /// rule|subject -> when the condition started holding (Unix s).
  final _since = <String, int>{};

  /// rule|subject episodes that already fired (or were held back).
  final _done = <String>{};

  /// Events up to here were looked at.
  DateTime? _evAt;

  void _save() {
    try {
      final tmp = File('$statePath.tmp');
      tmp.writeAsStringSync(jsonEncode({'since': _since, 'done': _done.toList(), 'ev': (_evAt?.millisecondsSinceEpoch ?? 0) ~/ 1000}));
      tmp.renameSync(statePath);
    } catch (_) {}
  }

  /// One pass. [reports]: every device's report (this one's included, as it
  /// is about to be written). [things]: this device's things.
  List<Firing> evaluate({required String root, required String me, required RuleSet rules, required Map<String, Report> reports, List<Report> things = const [], required DateTime now}) {
    final folder = SyncFolder(root);
    final self = reports[me];
    final evaluator = ruleEvaluator(reports, now);
    final forOthers = evaluator == me;
    final out = <Firing>[];
    final seen = <String>{};

    // Subjects this device answers for, per kind of trigger.
    final mine = <Report>[if (self != null) self, ...things];
    final others = forOthers ? [for (final r in reports.values) if (r.id != me && r.kind == 'android') r] : const <Report>[];
    final offlineSubjects = forOthers ? [for (final r in reports.values) if (!r.isThing && r.id != me) r] : const <Report>[];

    final evFrom = _evAt ?? now;
    final evDevices = [me, for (final r in others) r.id];
    final events = rules.rules.any((r) => r.enabled && r.trigger == 'event') && evFrom.isBefore(now)
        ? [for (final d in evDevices) ...readEvents(root, evFrom, now, device: d).where((e) => e.time.isAfter(evFrom) && !e.type.startsWith('rule.'))]
        : const <Event>[];
    _evAt = now;

    void consider(Rule rule, Report subj, bool holds, DateTime? start, String why) {
      final key = '${rule.id}|${subj.id}';
      seen.add(key);
      if (!holds) {
        _since.remove(key);
        _done.remove(key);
        return;
      }
      final since = _since[key] ??= ((start ?? now).millisecondsSinceEpoch ~/ 1000);
      final f = ((rule.when['for'] as num?) ?? (rule.trigger == 'offline' ? 600 : 0)).toInt();
      if (_done.contains(key) || now.millisecondsSinceEpoch ~/ 1000 - since < f) return;
      _done.add(key);
      final fired = _fire(folder, me, rule, subj, DateTime.fromMillisecondsSinceEpoch(since * 1000, isUtc: true), now, why);
      if (fired != null) out.add(fired);
    }

    for (final rule in rules.rules) {
      if (!rule.enabled) continue;
      final w = rule.when;
      switch (rule.trigger) {
        case 'value':
          for (final s in [...mine, ...others]) {
            if (!rule.covers(s.id, _typeOf(s), isThing: s.isThing)) continue;
            if (s.id != me && now.difference(s.time) > freshLimit(s)) continue;
            final v = ruleValue(s, '${w['path']}');
            final ok = _compare(v, '${w['op']}', w['value']);
            consider(rule, s, ok, null, '${s.name}: ${w['path']} is ${_show(v)} (${w['op']} ${w['value']})');
          }
        case 'unusual':
          for (final s in [...mine, ...others]) {
            if (!rule.covers(s.id, _typeOf(s), isThing: s.isThing)) continue;
            final u = s.readings.where((r) => r.key.startsWith('unusual.')).toList();
            consider(rule, s, u.isNotEmpty, null, '${s.name}: ${u.map((r) => r.value).join('; ')}');
          }
        case 'offline':
          for (final s in offlineSubjects) {
            if (!rule.covers(s.id, _typeOf(s))) continue;
            // The other system of a dual-boot machine is running: this one
            // is not booted, which is not "offline".
            final twinUp = reports.values.any((o) => o.id != s.id && sameMachine(o, s) && now.difference(o.time) <= freshLimit(o));
            final age = now.difference(s.time);
            final f = ((w['for'] as num?) ?? 600).toInt();
            consider(rule, s, !twinUp && age.inSeconds >= f, s.time, '${s.name}: no report for ${_age(age)} (last ${_hm(s.time)})');
          }
        case 'event':
          final pat = '${w['event']}';
          for (final e in events) {
            final match = pat.endsWith('*') ? e.type.startsWith(pat.substring(0, pat.length - 1)) : e.type == pat;
            if (!match) continue;
            final dev = reports[e.device];
            final thing = e.subject == null ? null : things.where((t) => t.id == e.subject).firstOrNull;
            final inScope = (dev != null && rule.covers(dev.id, dev.kind)) || (e.subject != null && rule.covers(e.subject!, thing != null ? _typeOf(thing) : 'thing', isThing: true));
            if (!inScope) continue;
            final subj = e.subject ?? e.device ?? me;
            final why = '${dev?.name ?? e.device}: ${e.type}${e.name != null ? ' ${e.name}' : ''}${e.detail != null ? ' (${e.detail})' : ''}';
            final f = _fire(folder, me, rule, thing ?? dev ?? reports[me]!, e.time, now, why, subjectId: subj);
            if (f != null) out.add(f);
          }
      }
    }
    // Episodes whose rule or subject went away.
    _since.removeWhere((k, _) => !seen.contains(k));
    _done.removeWhere((k) => !seen.contains(k));
    _save();
    return out;
  }

  Firing? _fire(SyncFolder folder, String me, Rule rule, Report subj, DateTime since, DateTime now, String why, {String? subjectId}) {
    final sid = subjectId ?? subj.id;
    final past = firedLines(folder.root, rule.id).where((l) => l['subject'] == sid);
    final sinceS = since.millisecondsSinceEpoch ~/ 1000;
    if (past.any((l) => l['since'] == sinceS)) return null; // another evaluator already did
    final last = past.map((l) => (l['t'] as num?)?.toInt() ?? 0).fold(0, (a, b) => a > b ? a : b);
    if (now.millisecondsSinceEpoch ~/ 1000 - last < rule.cooldown) return null;

    final did = <String>[];
    String? notify;
    List<String>? notifyOn;
    for (final a in rule.then) {
      switch (a['type']) {
        case 'notify':
          notify = a['text'] is String ? a['text'] as String : why;
          if (a['on'] is List) notifyOn = [...?notifyOn, for (final x in a['on'] as List) '$x'];
          did.add('notify');
        case 'annotate':
          try {
            appendEvents(folder.deviceDir(me), [Event(now, 'rule.fired', subject: sid, name: rule.name, detail: why)]);
            did.add('annotate');
          } catch (e) {
            did.add('annotate failed: $e');
          }
        case 'control':
          try {
            final req = ControlRequest(
                id: ControlRequest.newId(),
                from: me,
                fromName: 'rule ${rule.id}',
                host: a['host'] as String,
                target: a['target'] as String,
                key: a['key'] as String,
                value: a['value'] as String,
                time: now);
            folder.sendRequest(req);
            did.add('control ${req.key}=${req.value} sent to ${req.host} (request ${req.id})');
          } catch (e) {
            did.add('control failed: $e');
          }
      }
    }
    final f = Firing(rule: rule, subject: sid, since: since, time: now, why: why, did: did, notify: notify, notifyOn: notifyOn);
    _appendFired(folder.root, rule.id, me, f.toJson(me));
    return f;
  }
}

/// A value by path for rules: any [fieldAt] path, plus `disk.pct` and
/// `mem.pct`, worked out from used and total.
Object? ruleValue(Report s, String path) {
  // A report built in this process has no raw file yet.
  final j = s.raw.isNotEmpty ? s.raw : s.toJson();
  if (path == 'disk.pct' || path == 'mem.pct') {
    final u = fieldAt(j, '${path.substring(0, path.indexOf('.'))}.used'), t = fieldAt(j, '${path.substring(0, path.indexOf('.'))}.total');
    if (u is num && t is num && t > 0) return (1000 * u / t).round() / 10;
  }
  return fieldAt(j, path);
}

String _show(Object? v) => v == null ? 'unknown' : (v is double ? v.toStringAsFixed(1).replaceFirst(RegExp(r'\.0$'), '') : '$v');

bool _compare(Object? v, String op, Object? want) {
  if (v == null) return false; // unknown never fires
  if (v is bool || want is bool || want is String) {
    final a = '$v', b = '$want';
    return op == '==' ? a == b : (op == '!=' ? a != b : false);
  }
  final x = v is num ? v.toDouble() : double.tryParse('$v');
  final y = (want as num).toDouble();
  if (x == null) return false;
  return switch (op) { '<' => x < y, '<=' => x <= y, '>' => x > y, '>=' => x >= y, '==' => x == y, '!=' => x != y, _ => false };
}

String _firedDir(String root, String id) => '${RuleSet.dir(root)}${Platform.pathSeparator}$id${Platform.pathSeparator}fired';

/// Every firing of rule [id], from all evaluators, oldest first.
List<Map<String, Object?>> firedLines(String root, String id) {
  if (!validRuleDirId(id)) return const [];
  final d = Directory(_firedDir(root, id));
  if (!d.existsSync()) return const [];
  final out = <Map<String, Object?>>[
    for (final f in d.listSync().whereType<File>())
      if (f.path.endsWith('.jsonl') && !f.path.contains('.sync-conflict-')) ...readLines(f),
  ];
  out.sort((a, b) => ((a['t'] as num?) ?? 0).compareTo((b['t'] as num?) ?? 0));
  return out;
}

bool validRuleDirId(String id) => RegExp(r'^[a-z0-9_-][a-z0-9._-]{0,63}$').hasMatch(id) && !id.contains('..');

/// Firing logs stay small: over 256 KB, the older half goes.
const firedMaxBytes = 256 * 1024;

void _appendFired(String root, String id, String me, Map<String, Object?> line) {
  if (!validRuleDirId(id) || !ControlRequest.validName(me)) return;
  final d = Directory(_firedDir(root, id))..createSync(recursive: true);
  final f = File('${d.path}${Platform.pathSeparator}$me.jsonl');
  f.writeAsStringSync('${jsonEncode(line)}\n', mode: FileMode.append, flush: true);
  if (f.lengthSync() > firedMaxBytes) {
    final lines = f.readAsLinesSync();
    final tmp = File('${f.path}.tmp')..writeAsStringSync('${lines.skip(lines.length ~/ 2).join('\n')}\n', flush: true);
    tmp.renameSync(f.path);
  }
}
