import 'dart:io';

import 'events.dart';
import 'folder.dart';
import 'history.dart';

/// What the system looked like at a past moment (roadmap row 17), rebuilt
/// from the files every device already keeps: who was up, who reached whom,
/// which things were attached and which containers ran.
///
/// Within the detail window (7 days) a device counts as up at [t] when it
/// wrote a line within [slack] of it; older moments use the hourly summary
/// of that hour (up if it has one). Reachability is the device's own `p`
/// map (hourly: the share of the hour, reached when at least half).
/// Containers: the last `container.started` / `container.stopped` event
/// before [t], else running if the container wrote history then.
class DeviceAt {
  DeviceAt(this.id, {required this.up, this.at, this.reach = const {}, this.things = const [], this.containers = const {}});
  final String id;
  final bool up;

  /// The history line used (null when down).
  final DateTime? at;

  /// Peer id -> reached at that moment.
  final Map<String, bool> reach;

  /// Things that wrote history then (attached and on).
  final List<String> things;

  /// Container thing id -> "running" or "stopped".
  final Map<String, String> containers;
}

class Moment {
  Moment(this.t, this.devices, {required this.fromHourly});
  final DateTime t;
  final Map<String, DeviceAt> devices;

  /// True when [t] is older than the detail files and hourly summaries
  /// were used (coarser: whole hours).
  final bool fromHourly;

  List<String> get up => [for (final d in devices.values) if (d.up) d.id]..sort();
}

Map<String, Object?>? _near(String dir, DateTime t, Duration slack, bool hourly) {
  final sep = Platform.pathSeparator;
  final ts = t.millisecondsSinceEpoch ~/ 1000;
  if (hourly) {
    for (final n in historyNames(t, t, hourlyName, step: const Duration(days: 1))) {
      for (final l in readLines(File('$dir${sep}history${sep}hourly$sep$n'))) {
        final s = (l['t']! as num).toInt();
        if (s <= ts && ts < s + 3600) return l;
      }
    }
    return null;
  }
  // Devices write at their own pace (a phone every ~10 min at rest), so
  // "near" is 1.5 x this device's usual gap around [t], at least [slack].
  const look = Duration(hours: 1);
  final lines = [
    for (final n in historyNames(t.subtract(look), t.add(look), detailName)) ...readLines(File('$dir${sep}history${sep}detail$sep$n')),
  ]
    ..removeWhere((l) => ((l['t']! as num).toInt() - ts).abs() > look.inSeconds)
    ..sort((a, b) => (a['t']! as num).compareTo(b['t']! as num));
  final gaps = [for (var i = 1; i < lines.length; i++) (lines[i]['t']! as num).toInt() - (lines[i - 1]['t']! as num).toInt()]
    ..removeWhere((g) => g <= 0 || g > 1800)
    ..sort();
  final limit = gaps.isEmpty ? slack.inSeconds : (gaps[gaps.length ~/ 2] * 1.5).round().clamp(slack.inSeconds, 1800);
  Map<String, Object?>? best;
  var bestD = limit + 1;
  for (final l in lines) {
    final d = ((l['t']! as num).toInt() - ts).abs();
    if (d < bestD) {
      bestD = d;
      best = l;
    }
  }
  return best;
}

Moment topologyAt(String root, DateTime t, {DateTime? now, Duration slack = const Duration(minutes: 3), int detailDays = 7}) {
  final folder = SyncFolder(root);
  final hourly = (now ?? DateTime.now().toUtc()).difference(t) > Duration(days: detailDays);
  final events = readEvents(root, t.subtract(const Duration(days: 31)), t);
  final out = <String, DeviceAt>{};
  final dir = Directory(folder.devicesPath);
  if (!dir.existsSync()) return Moment(t, out, fromHourly: hourly);
  for (final d in dir.listSync().whereType<Directory>()) {
    final id = d.uri.pathSegments.where((x) => x.isNotEmpty).last;
    final line = _near(d.path, t, slack, hourly);
    if (line == null) {
      out[id] = DeviceAt(id, up: false);
      continue;
    }
    final reach = <String, bool>{};
    final p = line['p'];
    if (p is Map) {
      p.forEach((k, v) => reach['$k'] = v == 1);
    }
    for (final e in line.entries) {
      if (!e.key.startsWith('p.')) continue;
      final v = e.value;
      final avg = v is List && v.length == 3 && v[1] is num ? (v[1] as num) : (v is num ? v : null);
      if (avg != null) reach[e.key.substring(2)] = avg >= 0.5;
    }
    final things = <String>[];
    final containers = <String, String>{};
    for (final th in folder.thingIds(id)) {
      final on = _near(folder.thingDir(id, th), t, slack, hourly) != null;
      if (th.startsWith('ctr-')) {
        final last = events.where((e) => e.device == id && e.subject == th && (e.type == 'container.started' || e.type == 'container.stopped')).lastOrNull;
        containers[th] = last != null ? (last.type == 'container.started' ? 'running' : 'stopped') : (on ? 'running' : 'stopped');
      } else if (on) {
        things.add(th);
      }
    }
    out[id] = DeviceAt(id,
        up: true,
        at: DateTime.fromMillisecondsSinceEpoch((line['t']! as num).toInt() * 1000, isUtc: true),
        reach: reach,
        things: things..sort(),
        containers: containers);
  }
  return Moment(t, out, fromHourly: hourly);
}
