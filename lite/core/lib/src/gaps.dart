import 'dart:io';

import 'patterns.dart';

/// Holes in a device's history (row 4 of docs/ROADMAP.md): stretches with no
/// detail line for longer than three times the device's resting rate, and
/// what the files say about why.
class Gap {
  Gap(this.start, this.end);

  /// The last line before the hole and the first line after it.
  final DateTime start, end;
  Duration get length => end.difference(start);

  /// Share of the other devices' looks at this one during the hole that
  /// found it reachable (their history `p.<id>` = 1). null: nobody looked.
  double? reachable;

  /// Log lines from this device near the end of the hole that say the
  /// collector or the device started again (boot, update, agent start).
  final List<String> restarts = [];

  /// Errors this device logged during the hole.
  final List<String> errors = [];

  /// One word: "off" (nobody could reach it, or it booted), "collector"
  /// (others reached it, yet it wrote nothing: the collector's fault),
  /// "asleep" (Android, no restart: the system held the alarm back),
  /// "unknown".
  String get why {
    if (reachable != null && reachable! >= 0.5) return 'collector';
    if (restarts.any((l) => l.contains('BOOT_COMPLETED'))) return 'off';
    if (reachable != null && reachable! < 0.5) return 'off';
    if (android && restarts.isEmpty) return 'asleep';
    return 'unknown';
  }

  bool android = false;

  Map<String, Object?> toJson() => {
        'start': start.millisecondsSinceEpoch ~/ 1000,
        'end': end.millisecondsSinceEpoch ~/ 1000,
        'minutes': length.inMinutes,
        'why': why,
        if (reachable != null) 'reachable': (reachable! * 100).round() / 100,
        if (restarts.isNotEmpty) 'restarts': restarts,
        if (errors.isNotEmpty) 'errors': errors,
      };
}

/// Resting rate in seconds for a device kind: computers and servers write a
/// line a minute; Android every 5 minutes with the screen off.
int restSecondsFor(String? kind) => kind == 'android' ? 300 : 60;

/// Holes longer than 3 x [restSeconds] in [times] (Unix seconds, sorted),
/// within [from]..[to]. A hole at either end (the device not yet installed,
/// or silent right now) counts from/to those bounds only if [edges].
List<Gap> findGaps(List<int> times, int restSeconds, {required DateTime from, required DateTime to, bool edges = false}) {
  final limit = restSeconds * 3;
  final f = from.millisecondsSinceEpoch ~/ 1000, t = to.millisecondsSinceEpoch ~/ 1000;
  final ts = times.where((x) => x >= f && x <= t).toList();
  final out = <Gap>[];
  DateTime at(int s) => DateTime.fromMillisecondsSinceEpoch(s * 1000, isUtc: true);
  if (edges && ts.isNotEmpty && ts.first - f > limit) out.add(Gap(at(f), at(ts.first)));
  for (var i = 1; i < ts.length; i++) {
    if (ts[i] - ts[i - 1] > limit) out.add(Gap(at(ts[i - 1]), at(ts[i])));
  }
  if (edges && ts.isNotEmpty && t - ts.last > limit) out.add(Gap(at(ts.last), at(t)));
  return out;
}

/// A log line's time. Lines written from 2.6.7 on carry their UTC offset
/// ("2026-11-27T09:11:22-05:00"); older ones are local time with none, so
/// [assumeOffset] (the writing device's offset from UTC) is used.
DateTime? logTime(String line, Duration assumeOffset) {
  final m = RegExp(r'^(\d{4})-(\d\d)-(\d\d)T(\d\d):(\d\d):(\d\d)(Z|[+-]\d\d:?\d\d)?\s').firstMatch(line);
  if (m == null) return null;
  final local = DateTime.utc(int.parse(m[1]!), int.parse(m[2]!), int.parse(m[3]!), int.parse(m[4]!), int.parse(m[5]!), int.parse(m[6]!));
  final z = m[7];
  Duration off = assumeOffset;
  if (z == 'Z') {
    off = Duration.zero;
  } else if (z != null) {
    final d = z.replaceAll(':', '');
    final mins = int.parse(d.substring(1, 3)) * 60 + int.parse(d.substring(3, 5));
    off = Duration(minutes: d.startsWith('-') ? -mins : mins);
  }
  return local.subtract(off);
}

final _restart = RegExp(r'start agent|start \(|stop agent|update:|install');

/// Adds what the files say to each gap of device [id]: other devices'
/// detail history (their `p.<id>` reachability, from [peerDetail]: peer id ->
/// lines) and this device's [log].
void explainGaps(List<Gap> gaps, String id, {required Map<String, List<Map<String, Object?>>> peerDetail, String log = '', Duration logOffset = Duration.zero, bool android = false}) {
  final lines = [
    for (final l in log.split('\n'))
      if (logTime(l, logOffset) case final t?) (t, l)
  ];
  for (final g in gaps) {
    g.android = android;
    final s = g.start.millisecondsSinceEpoch ~/ 1000, e = g.end.millisecondsSinceEpoch ~/ 1000;
    var looks = 0, ok = 0;
    for (final ls in peerDetail.values) {
      for (final l in ls) {
        final t = (l['t'] as num?)?.toInt() ?? 0;
        if (t <= s + 60 || t >= e - 60) continue;
        final p = l['p'];
        final v = p is Map ? p[id] : null;
        if (v is! num) continue;
        looks++;
        if (v >= 1) ok++;
      }
    }
    g.reachable = looks == 0 ? null : ok / looks;
    for (final (t, l) in lines) {
      if (t.isBefore(g.start) || t.isAfter(g.end.add(const Duration(minutes: 10)))) continue;
      if (_restart.hasMatch(l)) g.restarts.add(l.trim());
      if (l.contains('ERROR') && t.isBefore(g.end)) g.errors.add(l.trim());
    }
  }
}

/// Every device in a Monitor folder: its gaps over the last [days], explained.
/// [offsets]: device id -> UTC offset of its older log lines (no offset in them).
Map<String, List<Gap>> folderGaps(String root, DateTime now, {int days = 7, Map<String, Duration> offsets = const {}, Map<String, String> kinds = const {}}) {
  final sep = Platform.pathSeparator;
  final devs = Directory('$root${sep}devices');
  if (!devs.existsSync()) return {};
  final ids = [for (final d in devs.listSync()) if (d is Directory) d.path.split(sep).last];
  final detail = {for (final id in ids) id: readDetail('$root${sep}devices$sep$id', now, days: days)};
  final out = <String, List<Gap>>{};
  for (final id in ids) {
    final times = [for (final l in detail[id]!) (l['t']! as num).toInt()];
    if (times.isEmpty) continue;
    final kind = kinds[id];
    final g = findGaps(times, restSecondsFor(kind), from: now.subtract(Duration(days: days)), to: now);
    String log = '';
    try {
      log = File('$root${sep}devices$sep$id${sep}log.txt').readAsStringSync();
    } catch (_) {}
    explainGaps(g, id, peerDetail: {for (final o in ids) if (o != id) o: detail[o]!}, log: log, logOffset: offsets[id] ?? Duration.zero, android: kind == 'android');
    out[id] = g;
  }
  return out;
}
