import 'package:monitor_core/monitor_core.dart';

/// The network as it was at an earlier time, rebuilt from each device's own
/// history lines (one a minute: battery, CPU, memory, and which other devices
/// it reached). History arrives in 15-minute batches, so the last quarter
/// hour is only in the live view.
class Timeline {
  Timeline(this.folder);
  final SyncFolder folder;

  final _days = <String, List<Map<String, Object?>>>{};
  DateTime? _loadedAt;

  /// Reads the last 24 h of every device once, then again at most every
  /// 5 minutes while the map is open.
  void load(Iterable<String> ids, DateTime now) {
    if (_loadedAt != null && now.difference(_loadedAt!) < const Duration(minutes: 5) && ids.every(_days.containsKey)) return;
    _loadedAt = now;
    for (final id in ids) {
      try {
        _days[id] = readHistoryRange(folder.deviceDir(id), HistoryRange.day, now);
      } catch (_) {
        _days[id] = const [];
      }
    }
  }

  /// The earliest time any device has history for, within the last day.
  DateTime? get earliest {
    int? t;
    for (final l in _days.values) {
      if (l.isEmpty) continue;
      final x = (l.first['t']! as num).toInt();
      if (t == null || x < t) t = x;
    }
    return t == null ? null : DateTime.fromMillisecondsSinceEpoch(t * 1000, isUtc: true);
  }

  /// Every device as it reported nearest to [at] (within 10 minutes), or
  /// marked silent when it wrote nothing around then.
  List<DeviceStatus> at(List<Report> devices, DateTime at) {
    final out = <DeviceStatus>[];
    final ts = at.millisecondsSinceEpoch ~/ 1000;
    for (final r in devices) {
      final lines = _days[r.id] ?? const [];
      Map<String, Object?>? best;
      var bestD = 601;
      for (final l in lines) {
        final d = ((l['t']! as num).toInt() - ts).abs();
        if (d < bestD) {
          bestD = d;
          best = l;
        }
      }
      if (best == null) {
        out.add(DeviceStatus(Report(id: r.id, name: r.name, kind: r.kind, time: at), Level.inactive, 'No readings then',
            'Nothing from ${r.name} within 10 minutes of this time.',
            fresh: false, age: Duration.zero));
        continue;
      }
      final p = best['p'];
      final peers = <String, PeerView>{
        if (p is Map)
          for (final e in p.entries)
            if (e.key is String) e.key as String: PeerView(tcp: e.value == 1 ? 'open' : 'timeout'),
      };
      final rep = Report(
        id: r.id,
        name: r.name,
        kind: r.kind,
        time: DateTime.fromMillisecondsSinceEpoch((best['t']! as num).toInt() * 1000, isUtc: true),
        battery: best['b'] is num ? Battery(pct: (best['b']! as num).toDouble(), charging: best['c'] == 1) : null,
        cpuPct: best['cpu'] is num ? (best['cpu']! as num).toDouble() : null,
        peers: peers,
      );
      final reached = peers.values.where((v) => v.reachable).length;
      final level = peers.isEmpty || reached == peers.length ? Level.ok : reached == 0 ? Level.fault : Level.warn;
      out.add(DeviceStatus(rep, level, peers.isEmpty || reached == peers.length ? 'Reached all' : reached == 0 ? 'Reached none' : 'Reached $reached of ${peers.length}', '${r.name} reached $reached of ${peers.length} devices.',
          fresh: true, age: Duration.zero));
    }
    return out;
  }
}
