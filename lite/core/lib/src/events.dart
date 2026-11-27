import 'dart:convert';
import 'dart:io';

import 'history.dart' show readLines;
import 'report.dart';
import 'status.dart' show freshLimit;

/// Things that happened, once each, on the device that saw them (row 7 of
/// docs/ROADMAP.md, FORMAT.md "Events"):
///
///   devices/<id>/events/YYYY-MM.jsonl   (UTC month)
///   {"t": 1795788000, "type": "peer.offline", "subject": "e1b8ee956de3",
///    "name": "wc-pixel", "detail": "last report 6 min ago"}
///
/// Types: peer.offline / peer.online (as this device sees the other's
/// reports), power.battery / power.mains (a UPS this device reads),
/// container.stopped / container.started / container.unhealthy,
/// thing.plugged / thing.unplugged, app.updated, control.applied.
class Event {
  Event(this.time, this.type, {this.subject, this.name, this.detail, this.device});
  final DateTime time;
  final String type;
  final String? subject, name, detail;

  /// The device that wrote it (filled in by [readEvents]).
  final String? device;

  Map<String, Object?> toJson() => {
        't': time.millisecondsSinceEpoch ~/ 1000,
        'type': type,
        if (subject != null) 'subject': subject,
        if (name != null) 'name': name,
        if (detail != null) 'detail': detail,
      };

  static Event? fromJson(Map<String, Object?> j, {String? device}) {
    final t = j['t'], type = j['type'];
    if (t is! num || type is! String) return null;
    return Event(DateTime.fromMillisecondsSinceEpoch(t.toInt() * 1000, isUtc: true), type,
        subject: j['subject'] as String?, name: j['name'] as String?, detail: j['detail'] as String?, device: device);
  }
}

String eventsFileName(DateTime t) => '${t.toUtc().year}-${t.toUtc().month.toString().padLeft(2, '0')}.jsonl';

void appendEvents(String deviceDir, List<Event> events) {
  if (events.isEmpty) return;
  final sep = Platform.pathSeparator;
  final d = Directory('$deviceDir${sep}events')..createSync(recursive: true);
  final byFile = <String, StringBuffer>{};
  for (final e in events) {
    (byFile[eventsFileName(e.time)] ??= StringBuffer()).writeln(jsonEncode(e.toJson()));
  }
  for (final f in byFile.entries) {
    File('${d.path}$sep${f.key}').writeAsStringSync(f.value.toString(), mode: FileMode.append, flush: true);
  }
}

/// Deletes event months that ended more than [keepDays] ago.
void pruneEvents(String deviceDir, DateTime now, int keepDays) {
  final d = Directory('$deviceDir${Platform.pathSeparator}events');
  if (!d.existsSync()) return;
  final cut = now.toUtc().subtract(Duration(days: keepDays));
  for (final f in d.listSync()) {
    final m = RegExp(r'(\d{4})-(\d\d)\.jsonl$').firstMatch(f.path);
    if (m == null && !f.path.contains('.sync-conflict-')) continue;
    if (m != null) {
      final end = DateTime.utc(int.parse(m[1]!), int.parse(m[2]!) + 1);
      if (!end.isBefore(cut)) continue;
    }
    try {
      f.deleteSync();
    } catch (_) {}
  }
}

/// Events from every device (or just [device]) between [from] and [to],
/// oldest first.
List<Event> readEvents(String root, DateTime from, DateTime to, {String? device}) {
  final sep = Platform.pathSeparator;
  final devs = Directory('$root${sep}devices');
  if (!devs.existsSync()) return const [];
  final months = <String>{};
  for (var m = DateTime.utc(from.toUtc().year, from.toUtc().month); !m.isAfter(to.toUtc()); m = DateTime.utc(m.year, m.month + 1)) {
    months.add(eventsFileName(m));
  }
  final out = <Event>[];
  for (final d in devs.listSync()) {
    if (d is! Directory) continue;
    final id = d.uri.pathSegments.where((s) => s.isNotEmpty).last;
    if (device != null && id != device) continue;
    for (final m in months) {
      for (final l in readLines(File('${d.path}${sep}events$sep$m'))) {
        final e = Event.fromJson(l, device: id);
        if (e != null && !e.time.isBefore(from) && !e.time.isAfter(to)) out.add(e);
      }
    }
  }
  out.sort((a, b) => a.time.compareTo(b.time));
  return out;
}

/// What a device can see at one moment, reduced to the values whose
/// changes are events. Kept between cycles (and restarts) in a small file.
class EventState {
  EventState([Map<String, String>? v, Map<String, String>? names])
      : values = v ?? {},
        names = names ?? {};
  final Map<String, String> values;
  final Map<String, String> names;

  static EventState of({required String app, required Map<String, Report> others, required List<Report> things, required DateTime now}) {
    final s = EventState();
    s.values['app'] = app;
    for (final o in others.values) {
      s.values['peer:${o.id}'] = now.difference(o.time) > freshLimit(o) ? 'stale' : 'fresh';
      s.names[o.id] = o.name;
    }
    for (final t in things) {
      s.names[t.id] = t.name;
      s.values['thing:${t.id}'] = t.connected == false ? 'unplugged' : 'plugged';
      String? r(String k) {
        for (final x in t.readings) {
          if (x.key == k) return x.value?.toString();
        }
        return null;
      }

      final st = r('ct.state'), health = r('ct.health'), ups = r('ups.status');
      if (st != null) s.values['ct:${t.id}'] = st;
      if (health != null) s.values['health:${t.id}'] = health;
      if (ups != null) s.values['ups:${t.id}'] = ups.startsWith('battery') ? 'battery' : (ups == 'mains' ? 'mains' : ups);
    }
    return s;
  }

  Map<String, Object?> toJson() => {'values': values, 'names': names};
  static EventState fromJson(Object? j) {
    if (j is! Map) return EventState();
    Map<String, String> m(Object? x) => {if (x is Map) for (final e in x.entries) if (e.key is String && e.value is String) e.key as String: e.value as String};
    return EventState(m(j['values']), m(j['names']));
  }

  static EventState load(String path) {
    try {
      return fromJson(jsonDecode(File(path).readAsStringSync()));
    } catch (_) {
      return EventState();
    }
  }

  void save(String path) {
    try {
      final tmp = File('$path.tmp');
      tmp.writeAsStringSync(jsonEncode(toJson()));
      tmp.renameSync(path);
    } catch (_) {}
  }
}

/// The events between two states. A key seen for the first time is not an
/// event (nothing changed that we know of), except a newly plugged thing.
/// [peers] false: this device itself was just asleep or restarted, so the
/// other devices' files are not yet synced and their freshness says
/// nothing; peer events are skipped this time.
List<Event> diffEvents(EventState before, EventState now, DateTime t, {bool peers = true}) {
  final out = <Event>[];
  final isFirst = before.values.isEmpty;
  if (isFirst) return out;
  for (final e in now.values.entries) {
    final old = before.values[e.key];
    if (old == e.value) continue;
    final colon = e.key.indexOf(':');
    final kind = colon < 0 ? e.key : e.key.substring(0, colon);
    final id = colon < 0 ? null : e.key.substring(colon + 1);
    final name = id == null ? null : (now.names[id] ?? before.names[id]);
    switch (kind) {
      case 'app':
        if (old != null) out.add(Event(t, 'app.updated', detail: '$old -> ${e.value}'));
      case 'peer':
        if (old != null && peers) out.add(Event(t, e.value == 'stale' ? 'peer.offline' : 'peer.online', subject: id, name: name));
      case 'thing':
        if (old != null || e.value == 'plugged') out.add(Event(t, e.value == 'unplugged' ? 'thing.unplugged' : 'thing.plugged', subject: id, name: name));
      case 'ct':
        if (old == null) break;
        if (e.value == 'running') {
          out.add(Event(t, 'container.started', subject: id, name: name, detail: 'was $old'));
        } else if (old == 'running') {
          out.add(Event(t, 'container.stopped', subject: id, name: name, detail: e.value));
        }
      case 'health':
        if (e.value == 'unhealthy') out.add(Event(t, 'container.unhealthy', subject: id, name: name));
      case 'ups':
        if (old == null) break;
        if (e.value == 'battery') out.add(Event(t, 'power.battery', subject: id, name: name, detail: 'mains power lost'));
        if (e.value == 'mains' && old == 'battery') out.add(Event(t, 'power.mains', subject: id, name: name, detail: 'mains power back'));
    }
  }
  // Things that vanished from the scan entirely (not just marked unplugged).
  for (final k in before.values.keys) {
    if (k.startsWith('thing:') && !now.values.containsKey(k) && before.values[k] == 'plugged') {
      final id = k.substring(6);
      out.add(Event(t, 'thing.unplugged', subject: id, name: before.names[id]));
    }
  }
  return out;
}
