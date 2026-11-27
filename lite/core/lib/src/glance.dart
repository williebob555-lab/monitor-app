import 'report.dart';
import 'status.dart';
import 'topology.dart';

/// One line for Home: the few things Wilson wants without opening anything
/// (2026-11-27: "I hate how many submenus I have to go through just to read
/// off the UPS status"). UPS first, then failing disks.
class GlanceLine {
  GlanceLine(this.thingId, this.level, this.label, this.text);
  final String thingId;
  final Level level;

  /// "UPS", "Disk".
  final String label;

  /// "On mains, 100 %, 70 min" / "On battery, 64 %, 22 min left".
  final String text;
}

String? _v(Report r, String k) {
  final x = r.readings.where((x) => x.key == k).firstOrNull;
  if (x == null || x.value == null) return null;
  final v = x.value;
  return v is num ? (v == v.roundToDouble() ? '${v.round()}' : v.toStringAsFixed(1)) : '$v';
}

Level _lv(Report r, String k) => switch (r.readings.where((x) => x.key == k).firstOrNull?.level) { 'fault' => Level.fault, 'warn' => Level.warn, _ => Level.ok };

/// Glance lines from the things on reporting hosts. Old copies of a thing
/// (two systems wrote it) collapse to the newest by id.
List<GlanceLine> glance(Map<String, Thing> things, {DateTime? now}) {
  final t = now ?? DateTime.now().toUtc();
  final out = <GlanceLine>[];
  final ups = things.values.where((x) => x.report.node?.type == 'ups').toList()..sort((a, b) => b.report.time.compareTo(a.report.time));
  final seen = <String>{};
  for (final u in ups) {
    if (!seen.add(u.id)) continue;
    final r = u.report;
    final stale = t.difference(r.time) > freshLimit(r) + const Duration(minutes: 2);
    final status = _v(r, 'ups.status'), charge = _v(r, 'ups.charge'), runtime = _v(r, 'ups.runtime');
    if (stale || status == null) {
      out.add(GlanceLine(u.id, Level.inactive, 'UPS', 'not reported${stale ? ' since ${r.time.toLocal().toString().substring(11, 16)}' : ''}'));
      continue;
    }
    final onBattery = status.startsWith('battery');
    final parts = [
      onBattery ? 'On battery${status.contains('low') ? ' (low)' : ''}' : 'On mains',
      if (charge != null) '$charge %',
      if (runtime != null) '$runtime min${onBattery ? ' left' : ''}',
    ];
    final level = status.contains('low') ? Level.fault : (onBattery ? Level.warn : _lv(r, 'ups.status'));
    out.add(GlanceLine(u.id, level, ups.length > 1 ? 'UPS ${r.name}' : 'UPS', parts.join(', ')));
  }
  for (final d in things.values.where((x) => x.report.node?.type == 'disk')) {
    final r = d.report;
    final bad = r.readings.where((x) => x.level == 'fault' || x.level == 'warn').toList();
    if (bad.isEmpty || !seen.add(d.id)) continue;
    out.add(GlanceLine(d.id, bad.any((x) => x.level == 'fault') ? Level.fault : Level.warn, 'Disk',
        '${r.name}: ${bad.map((x) => '${x.label} ${x.value}${x.unit != null ? ' ${x.unit}' : ''}').join(', ')}'));
  }
  return out;
}
