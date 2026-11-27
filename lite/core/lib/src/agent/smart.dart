import 'dart:convert';
import 'dart:io';

import '../report.dart';
import '../topology.dart';

/// Disk health (SMART) from the host companion (roadmap row 38,
/// docs/COMPANION.md). The collector never touches a disk: a sandboxed root
/// timer on the host (packaging/linux/smart-export.sh) writes one small
/// `smartctl -j` file per disk into a folder the collector reads:
/// `/host/smart` in Docker (mounted read-only), else
/// `/var/lib/monitor-app/smart`, or `MONITOR_SMART_DIR`.
///
/// Each disk becomes a thing of type `disk` whose id comes from its model
/// and serial, so both systems of a dual-boot machine write the same disk.
class Smart {
  static const limitBytes = 64 * 1024;

  /// An export older than this is shown as unknown, not as the old numbers
  /// (the timer runs every 30 minutes).
  static const staleAfter = Duration(minutes: 75);

  static String? folder() {
    final env = Platform.environment['MONITOR_SMART_DIR'];
    for (final d in [if (env != null && env.isNotEmpty) env, '/host/smart', '/var/lib/monitor-app/smart']) {
      if (Directory(d).existsSync()) return d;
    }
    return null;
  }

  List<Report> collect(String hostId, String? machine, DateTime now, int next) {
    final dir = folder();
    if (dir == null) return const [];
    final out = <Report>[];
    try {
      for (final f in Directory(dir).listSync().whereType<File>()) {
        final name = f.uri.pathSegments.last;
        if (!name.startsWith('smart-') || !name.endsWith('.json')) continue;
        if (f.lengthSync() > limitBytes) continue;
        final j = jsonDecode(f.readAsStringSync());
        if (j is! Map) continue;
        final r = diskReport(j.cast<String, Object?>(), hostId, machine, now, next);
        if (r != null) out.add(r);
      }
    } catch (_) {}
    return out;
  }

  static String _slug(String s) => s.toLowerCase().replaceAll(RegExp(r'[^a-z0-9]+'), '-').replaceAll(RegExp(r'^-|-$'), '');

  /// One export file -> one `disk` thing. The file is the export script's
  /// wrapper: `{"exported": unix, "device": "/dev/sda", "smart": <smartctl -j>}`
  /// or `{"exported": unix, "device": ..., "error": "why"}`.
  static Report? diskReport(Map<String, Object?> j, String hostId, String? machine, DateTime now, int next) {
    final exported = j['exported'] is num ? DateTime.fromMillisecondsSinceEpoch((j['exported']! as num).toInt() * 1000, isUtc: true) : null;
    final dev = '${j['device'] ?? '?'}';
    final s = j['smart'] is Map ? (j['smart']! as Map).cast<String, Object?>() : const <String, Object?>{};
    final model = (s['model_name'] ?? s['scsi_model_name'])?.toString().trim();
    final serial = s['serial_number']?.toString().trim();
    final id = model != null && serial != null ? 'disk-${_slug(model)}-${_slug(serial)}' : 'disk-${_slug(hostId)}-${_slug(dev)}';
    final age = exported == null ? null : now.difference(exported);
    final stale = age == null || age > staleAfter;
    final why = j['error']?.toString() ?? (stale ? (exported == null ? 'no export time' : 'last export ${age!.inMinutes} min ago') : null);
    final exit = ((s['smartctl'] as Map?)?['exit_status'] as num?)?.toInt() ?? 0;
    // smartctl -n standby: bit 1 set and no data = the disk was asleep and
    // was left asleep.
    final asleep = s['smart_status'] == null && exit & 2 != 0;

    Reading r(String key, String label, Object? v, {String? unit, String? level, String? note}) => Reading(
        key: key,
        label: label,
        value: why != null ? null : v,
        unit: unit,
        group: 'Disk',
        src: why != null || v == null ? 'unknown' : 'measured',
        level: why != null ? null : level,
        note: why ?? note);

    final passed = (s['smart_status'] as Map?)?['passed'];
    final temp = ((s['temperature'] as Map?)?['current'] as num?)?.toDouble();
    final hours = ((s['power_on_time'] as Map?)?['hours'] as num?)?.toInt();
    final nvme = (s['nvme_smart_health_information_log'] as Map?)?.cast<String, Object?>();
    final attrs = <int, num>{
      for (final a in ((s['ata_smart_attributes'] as Map?)?['table'] as List?) ?? const [])
        if (a is Map && a['id'] is num && (a['raw'] as Map?)?['value'] is num) (a['id'] as num).toInt(): (a['raw'] as Map)['value'] as num,
    };
    final wear = (nvme?['percentage_used'] as num?) ?? _ataWear(s);
    final realloc = attrs[5], pending = attrs[197], uncorrectable = attrs[198];
    final mediaErr = nvme?['media_errors'] as num?;
    final rotation = s['rotation_rate'];
    final cap = ((s['user_capacity'] as Map?)?['bytes'] as num?)?.toDouble();
    final kind = nvme != null || dev.contains('nvme') ? 'NVMe SSD' : (rotation is num && rotation > 0 ? 'Hard disk' : 'SSD');

    return Report(
      id: id,
      name: model ?? dev,
      kind: 'thing',
      time: now,
      machine: machine,
      nextSeconds: next,
      connected: true,
      readings: [
        r('disk.health', 'Health', asleep ? 'asleep' : (passed == null ? null : (passed == true ? 'passed' : 'FAILING')),
            level: passed == false ? 'fault' : null, note: asleep ? 'left asleep; read next time it is awake' : null),
        if (!asleep) ...[
          r('disk.temp', 'Temperature', temp, unit: '°C', level: temp != null && temp >= 60 ? 'warn' : null),
          r('disk.hours', 'Powered on', hours, unit: 'h'),
          if (wear != null) r('disk.wear', 'Wear', wear, unit: '%', level: wear >= 90 ? 'warn' : null, note: 'of rated endurance used'),
          if (realloc != null) r('disk.realloc', 'Reallocated sectors', realloc, level: realloc > 0 ? 'warn' : null),
          if (pending != null) r('disk.pending', 'Pending sectors', pending, level: pending > 0 ? 'warn' : null),
          if (uncorrectable != null) r('disk.uncorrectable', 'Uncorrectable', uncorrectable, level: uncorrectable > 0 ? 'fault' : null),
          if (mediaErr != null) r('disk.mediaErrors', 'Media errors', mediaErr, level: mediaErr > 0 ? 'warn' : null),
        ],
        Reading(key: 'disk.kind', label: 'Type', value: kind, group: 'Disk', src: 'declared'),
        if (cap != null) Reading(key: 'disk.size', label: 'Size', value: (cap / 1e9).round(), unit: 'GB', group: 'Disk', src: 'declared'),
        if (serial != null) Reading(key: 'disk.serial', label: 'Serial', value: serial, group: 'Disk', src: 'declared'),
        Reading(key: 'disk.device', label: 'Device', value: dev, group: 'Disk', src: 'declared'),
      ],
      node: NodeDecl(id: id, type: 'disk', parent: hostId, links: [
        NodeLink(to: hostId, via: dev.contains('nvme') ? 'pcie' : 'sata', dir: 'both', detail: kind),
      ]),
    );
  }

  /// SATA SSD wear from the usual vendor attributes, as % used.
  static num? _ataWear(Map<String, Object?> s) {
    for (final a in ((s['ata_smart_attributes'] as Map?)?['table'] as List?) ?? const []) {
      if (a is! Map) continue;
      final id = (a['id'] as num?)?.toInt();
      final value = (a['value'] as num?)?.toInt();
      // 177 Wear_Leveling_Count (Samsung), 231 SSD_Life_Left, 233
      // Media_Wearout_Indicator: normalized value counts down from 100.
      if (value != null && (id == 177 || id == 231 || id == 233) && value <= 100) return 100 - value;
    }
    return null;
  }
}
