import 'dart:math' as math;

import 'report.dart';

/// "Unusual for this device" (roadmap row 16): the last hour against what
/// this device usually does at the same time of day, learned from its own
/// hourly summaries. Quiet by design:
///
/// * only with at least [minDays] earlier days that have that time of day;
/// * only when the last hour is far above the usual (over the highest of
///   1.5 x the 90th percentile and the usual plus a floor per value);
/// * at most [maxPerDay] new flags a day per device, kept by [UnusualLimiter].
///
/// Returns readings with `level: warn` and the reason in words, e.g. "CPU
/// 70 %, usually 8 % at night".
class UnusualCheck {
  const UnusualCheck(this.key, this.label, this.unit, this.floor);
  final String key, label, unit;

  /// The smallest rise worth a word (CPU 20 points, memory 15 ...).
  final double floor;

  static const all = [
    UnusualCheck('cpu', 'CPU', '%', 20),
    UnusualCheck('mem', 'Memory', '%', 15),
    UnusualCheck('temp.cpu', 'CPU temperature', '°C', 10),
    UnusualCheck('ct.mem', 'Memory', 'MB', 100),
  ];
}

const minDays = 3, maxPerDay = 3;

const _parts = ['at night', 'in the morning', 'in the afternoon', 'in the evening'];

double? _avg(Map<String, Object?> l, String k) {
  final v = l[k];
  if (v is num) return v.toDouble();
  if (v is List && v.length == 3 && v[1] is num) return (v[1] as num).toDouble();
  return null;
}

String _f(double v) => v >= 10 ? v.round().toString() : v.toStringAsFixed(1).replaceFirst(RegExp(r'\.0$'), '');

/// [hourly]: this device's hourly summaries (t = local hour start, Unix s).
/// [recent]: its detail lines from the last hour. [now]: local time is used
/// for the time of day, as the writer's file names are.
List<Reading> findUnusual(List<Map<String, Object?>> hourly, List<Map<String, Object?>> recent, DateTime now, {List<UnusualCheck> checks = UnusualCheck.all}) {
  final local = now.toLocal();
  final part = local.hour ~/ 6;
  final today = DateTime(local.year, local.month, local.day);
  final out = <Reading>[];
  for (final c in checks) {
    final cur = recent.map((l) => _avg(l, c.key)).whereType<double>().toList();
    if (cur.length < 20) continue; // most of an hour of readings
    final current = cur.reduce((a, b) => a + b) / cur.length;
    final base = <double>[];
    final days = <int>{};
    for (final l in hourly) {
      final t = DateTime.fromMillisecondsSinceEpoch((l['t']! as num).toInt() * 1000);
      if (!t.isBefore(today) || t.hour ~/ 6 != part) continue;
      final v = _avg(l, c.key);
      if (v == null) continue;
      base.add(v);
      days.add(DateTime(t.year, t.month, t.day).millisecondsSinceEpoch);
    }
    if (days.length < minDays) continue;
    base.sort();
    final usual = base[base.length ~/ 2];
    final p90 = base[math.min(base.length - 1, (base.length * 0.9).floor())];
    final limit = math.max(p90 * 1.5, usual + c.floor);
    if (current <= limit) continue;
    final sp = c.unit == '%' ? ' %' : ' ${c.unit}';
    out.add(Reading(
        key: 'unusual.${c.key}',
        label: 'Unusual',
        value: '${c.label} ${_f(current)}$sp, usually ${_f(usual)}$sp ${_parts[part]}',
        group: 'Unusual',
        src: 'estimated',
        level: 'warn',
        note: 'last hour against ${days.length} earlier days at this time of day'));
  }
  return out;
}

/// Keeps flags to [maxPerDay] new ones a day: a flag that is already shown
/// stays; a new one is dropped once the day's budget is used.
class UnusualLimiter {
  final _shown = <String>{};
  final _day = <String>[];
  int? _dayKey;

  List<Reading> apply(List<Reading> found, DateTime now) {
    final l = now.toLocal();
    final dk = l.year * 10000 + l.month * 100 + l.day;
    if (dk != _dayKey) {
      _dayKey = dk;
      _day.clear();
    }
    final keep = <Reading>[];
    for (final r in found) {
      if (_shown.contains(r.key)) {
        keep.add(r);
      } else if (_day.length < maxPerDay) {
        _day.add(r.key);
        keep.add(r);
      }
    }
    _shown
      ..clear()
      ..addAll(keep.map((r) => r.key));
    return keep;
  }
}
