import 'dart:convert';
import 'dart:io';
import 'dart:math' as math;

import 'history.dart';

/// What a device's history shows, in words: "battery drains 1.2 %/h with
/// the screen off", "CPU busiest in the afternoon". Built from the history
/// files every device already keeps (FORMAT.md "Patterns"), so any device
/// can work out any other device's patterns without asking it.
///
/// Every pattern says how much history it rests on ([hours]); one resting on
/// less than [minHours] is not shown at all, so a few minutes of data never
/// turn into a claim.
class Pattern {
  const Pattern({required this.key, required this.group, required this.text, required this.hours, this.value, this.unit, this.estimated = false});
  final String key, group, text;
  final double? value;
  final String? unit;

  /// Hours of history it is based on.
  final double hours;

  /// Worked out from measurements (like "a full charge lasts"), not measured.
  final bool estimated;

  Map<String, Object?> toJson() => {
        'key': key,
        'group': group,
        'text': text,
        if (value != null) 'value': value,
        if (unit != null) 'unit': unit,
        'hours': (hours * 10).roundToDouble() / 10,
        if (estimated) 'src': 'estimated',
      };
}

const minHours = 1.0;

/// history/patterns/YYYY.jsonl: one line per local day with every pattern's
/// value, `{"t": day start, "<pattern key>": value, ...}`. Patterns thus
/// outlast the detail and hourly files they were worked out from, and
/// `seriesOf(lines, key)` charts how one changes over months.
String patternsYearName(DateTime d) => '${d.year}.jsonl';

/// Writes today's line (replacing an earlier one from today).
void recordPatterns(String deviceDir, List<Pattern> list, DateTime now) {
  final sep = Platform.pathSeparator;
  final local = now.toLocal();
  final t = DateTime(local.year, local.month, local.day).millisecondsSinceEpoch ~/ 1000;
  final line = <String, Object?>{
    't': t,
    for (final p in list)
      if (p.value != null) p.key: (p.value! * 100).roundToDouble() / 100,
  };
  if (line.length == 1) return;
  final dir = Directory('$deviceDir${sep}history${sep}patterns')..createSync(recursive: true);
  final name = patternsYearName(local);
  final f = File('${dir.path}$sep$name');
  final lines = [...readLines(f)]
    ..removeWhere((l) => l['t'] == t)
    ..add(line)
    ..sort((a, b) => (a['t']! as num).compareTo(b['t']! as num));
  final tmp = File('${dir.path}$sep.syncthing.$name.tmp');
  tmp.writeAsStringSync('${lines.map(jsonEncode).join('\n')}\n', flush: true);
  tmp.renameSync(f.path);
}

/// The pattern lines of the last [years] years, oldest first.
List<Map<String, Object?>> readPatternHistory(String deviceDir, DateTime now, {int years = 5}) {
  final sep = Platform.pathSeparator;
  final y = now.toUtc().add(const Duration(hours: 14)).year;
  return [
    for (var i = years; i >= 0; i--) ...readLines(File('$deviceDir${sep}history${sep}patterns$sep${y - i}.jsonl')),
  ];
}

/// The last [days] of detail lines (one a minute or so), oldest first.
List<Map<String, Object?>> readDetail(String deviceDir, DateTime now, {int days = 7}) {
  final sep = Platform.pathSeparator;
  final from = now.subtract(Duration(days: days));
  final out = <Map<String, Object?>>[];
  // Named in the writing device's local time: see historyNames.
  for (final n in historyNames(from, now, detailName)) {
    out.addAll(readLines(File('$deviceDir${sep}history${sep}detail$sep$n')));
  }
  final f = from.millisecondsSinceEpoch ~/ 1000;
  out.removeWhere((l) => (l['t']! as num) < f);
  out.sort((a, b) => (a['t']! as num).compareTo(b['t']! as num));
  return out;
}

/// The hour starts a device reported in, from its hourly summaries.
Set<int> hoursUp(List<Map<String, Object?>> hourly) => {for (final l in hourly) (l['t']! as num).toInt()};

/// The last [days] of hourly summary lines, oldest first.
List<Map<String, Object?>> readHourly(String deviceDir, DateTime now, {int days = 30}) {
  final sep = Platform.pathSeparator;
  final from = now.subtract(Duration(days: days));
  final out = <Map<String, Object?>>[];
  for (final m in historyNames(from, now, hourlyName, step: const Duration(days: 1))) {
    out.addAll(readLines(File('$deviceDir${sep}history${sep}hourly$sep$m')));
  }
  final f = from.millisecondsSinceEpoch ~/ 1000;
  out.removeWhere((l) => (l['t']! as num) < f);
  out.sort((a, b) => (a['t']! as num).compareTo(b['t']! as num));
  return out;
}

/// Patterns in one device's history. [names] turns peer ids into names.
///
/// [upHours] (peer id -> the hour starts it reported in, from its own hourly
/// file) leaves out hours a peer was off: not reaching a device that was
/// switched off is not a connection problem.
List<Pattern> findPatterns(List<Map<String, Object?>> detail, List<Map<String, Object?>> hourly, {Map<String, String> names = const {}, Map<String, Set<int>>? upHours}) {
  return [
    ..._battery(detail),
    ..._byTimeOfDay(hourly, 'cpu', 'Processor', 'CPU'),
    ..._usual(hourly, 'mem', 'Memory', 'Memory'),
    ..._usual(hourly, 'temp.cpu', 'Temperature', 'CPU temperature', unit: '°C'),
    ..._traffic(hourly),
    ..._reach(hourly, names, upHours),
  ];
}

num? _num(Map<String, Object?> l, String k) => l[k] is num ? l[k] as num : null;

/// The average of a summary value ([min, avg, max]) or a plain number.
double? _avg(Map<String, Object?> l, String k) {
  final v = l[k];
  if (v is num) return v.toDouble();
  if (v is List && v.length == 3 && v[1] is num) return (v[1] as num).toDouble();
  return null;
}

double? _max(Map<String, Object?> l, String k) {
  final v = l[k];
  if (v is num) return v.toDouble();
  if (v is List && v.length == 3 && v[2] is num) return (v[2] as num).toDouble();
  return null;
}

String _f(double v) => v >= 10 ? v.round().toString() : v.toStringAsFixed(1);

String _h(double h) => h >= 48 ? '${(h / 24).round()} days' : '${_f(h)} h';

/// Battery: how fast it drains (by screen) and charges, from consecutive
/// readings no more than 20 minutes apart under the same condition.
List<Pattern> _battery(List<Map<String, Object?>> lines) {
  // condition -> [percent change, seconds]
  final acc = <String, List<double>>{};
  for (var i = 1; i < lines.length; i++) {
    final a = lines[i - 1], b = lines[i];
    final ba = _num(a, 'b'), bb = _num(b, 'b');
    final dt = (b['t']! as num) - (a['t']! as num);
    if (ba == null || bb == null || dt <= 0 || dt > 1200) continue;
    final ca = _num(a, 'c'), cb = _num(b, 'c');
    if (ca == null || ca != cb) continue;
    final String cond;
    if (ca == 1) {
      if (ba >= 99) continue; // full: not charging any more
      cond = 'charge';
    } else {
      // Held at 100 % "not charging" is a full battery on mains, not drain.
      if (ba >= 99 && bb >= 99) continue;
      final sa = _num(a, 's'), sb = _num(b, 's');
      cond = sa == null ? 'drain' : (sa != sb ? '' : (sa == 1 ? 'drain.on' : 'drain.off'));
      if (cond.isEmpty) continue;
    }
    final x = acc.putIfAbsent(cond, () => [0, 0]);
    x[0] += (bb - ba).toDouble();
    x[1] += dt.toDouble();
  }
  final out = <Pattern>[];
  for (final e in acc.entries) {
    final hours = e.value[1] / 3600;
    if (hours < minHours) continue;
    final rate = e.value[0] / hours;
    final over = '(over ${_h(hours)})';
    switch (e.key) {
      case 'charge':
        if (rate <= 0) continue;
        out.add(Pattern(key: 'battery.charge', group: 'Battery', text: 'Charges ${_f(rate)} %/h $over', value: rate, unit: '%/h', hours: hours));
      default:
        final drain = -rate;
        final when = switch (e.key) { 'drain.on' => ' with the screen on', 'drain.off' => ' with the screen off', _ => ' on battery' };
        if (drain <= 0.05) {
          out.add(Pattern(key: 'battery.${e.key}', group: 'Battery', text: 'Hardly drains$when $over', value: 0, unit: '%/h', hours: hours));
          continue;
        }
        out.add(Pattern(key: 'battery.${e.key}', group: 'Battery', text: 'Drains ${_f(drain)} %/h$when $over', value: drain, unit: '%/h', hours: hours));
        out.add(Pattern(key: 'battery.${e.key}.lasts', group: 'Battery', text: 'A full charge would last about ${_h(100 / drain)}$when', value: 100 / drain, unit: 'h', hours: hours, estimated: true));
    }
  }
  return out;
}

const _parts = ['at night', 'in the morning', 'in the afternoon', 'in the evening'];

/// When in the day a value runs highest and lowest (local time, 6-hour parts).
List<Pattern> _byTimeOfDay(List<Map<String, Object?>> hourly, String k, String group, String label) {
  final sum = List<double>.filled(4, 0), n = List<int>.filled(4, 0);
  for (final l in hourly) {
    final v = _avg(l, k);
    if (v == null) continue;
    final part = DateTime.fromMillisecondsSinceEpoch((l['t']! as num).toInt() * 1000).hour ~/ 6;
    sum[part] += v;
    n[part]++;
  }
  final parts = [
    for (var i = 0; i < 4; i++)
      if (n[i] >= 2) i
  ];
  final hours = n.fold<int>(0, (a, b) => a + b).toDouble();
  if (parts.length < 2 || hours < 6) return const [];
  double avg(int i) => sum[i] / n[i];
  // A server in Docker often runs on UTC: say so rather than call 3 AM "afternoon".
  final zone = DateTime.now().timeZoneOffset == Duration.zero ? ' (UTC)' : '';
  final hi = parts.reduce((a, b) => avg(a) >= avg(b) ? a : b);
  final lo = parts.reduce((a, b) => avg(a) <= avg(b) ? a : b);
  if (avg(hi) - avg(lo) < 2) {
    return [Pattern(key: '$k.steady', group: group, text: '$label is about ${_f(avg(hi))} % all day (over ${_h(hours)})', value: avg(hi), unit: '%', hours: hours)];
  }
  return [
    Pattern(
        key: '$k.busiest',
        group: group,
        text: '$label is busiest ${_parts[hi]} (${_f(avg(hi))} %) and quietest ${_parts[lo]} (${_f(avg(lo))} %)$zone, over ${_h(hours)}',
        value: avg(hi),
        unit: '%',
        hours: hours),
  ];
}

/// The usual level (median of hourly averages) and the highest seen.
List<Pattern> _usual(List<Map<String, Object?>> hourly, String k, String group, String label, {String unit = '%'}) {
  final avgs = hourly.map((l) => _avg(l, k)).whereType<double>().toList()..sort();
  if (avgs.length < 3) return const [];
  final peak = hourly.map((l) => _max(l, k)).whereType<double>().fold<double>(-1e9, math.max);
  final usual = avgs[avgs.length ~/ 2];
  final hours = avgs.length.toDouble();
  final sp = unit == '%' ? ' %' : ' $unit';
  return [Pattern(key: '$k.usual', group: group, text: '$label is usually ${_f(usual)}$sp, highest ${_f(peak)}$sp (over ${_h(hours)})', value: usual, unit: unit, hours: hours)];
}

/// Data used per day, from the hourly average rates.
List<Pattern> _traffic(List<Map<String, Object?>> hourly) {
  var rx = 0.0, tx = 0.0, hours = 0;
  for (final l in hourly) {
    final r = _avg(l, 'rx'), t = _avg(l, 'tx');
    if (r == null || t == null) continue;
    rx += r * 3600;
    tx += t * 3600;
    hours++;
  }
  if (hours < 6) return const [];
  String size(double bytesPerDay) => bytesPerDay >= 1e9
      ? '${_f(bytesPerDay / 1e9)} GB'
      : bytesPerDay >= 1e6
          ? '${_f(bytesPerDay / 1e6)} MB'
          : '${_f(bytesPerDay / 1e3)} KB';
  final perDay = 24 / hours;
  return [
    Pattern(
        key: 'net.perDay',
        group: 'Network',
        text: 'Downloads about ${size(rx * perDay)} and uploads ${size(tx * perDay)} a day (over ${_h(hours.toDouble())})',
        value: (rx + tx) * perDay,
        unit: 'B/day',
        hours: hours.toDouble()),
  ];
}

/// How often it reached each other device (Tailscale), from hourly lines.
List<Pattern> _reach(List<Map<String, Object?>> hourly, Map<String, String> names, Map<String, Set<int>>? upHours) {
  final sum = <String, double>{}, n = <String, int>{};
  for (final l in hourly) {
    for (final e in l.entries) {
      if (!e.key.startsWith('p.')) continue;
      final v = _avg(l, e.key);
      if (v == null) continue;
      final id = e.key.substring(2);
      final up = upHours?[id];
      if (upHours != null && (up == null || !up.contains((l['t']! as num).toInt()))) continue;
      sum[id] = (sum[id] ?? 0) + v;
      n[id] = (n[id] ?? 0) + 1;
    }
  }
  final always = <String>[];
  final out = <Pattern>[];
  for (final id in sum.keys) {
    final hours = n[id]!.toDouble();
    if (hours < 6) continue;
    final pct = 100 * sum[id]! / hours;
    final name = names[id] ?? id;
    if (pct >= 99) {
      always.add(name);
    } else {
      out.add(Pattern(
          key: 'reach.$id',
          group: 'Connections',
          text: pct < 1 ? 'Never reached $name (over ${_h(hours)})' : 'Reached $name ${_f(pct)} % of the time (over ${_h(hours)})',
          value: pct,
          unit: '%',
          hours: hours));
    }
  }
  if (always.isNotEmpty) {
    always.sort();
    final hours = n.values.fold<int>(0, math.max).toDouble();
    out.insert(0, Pattern(key: 'reach.always', group: 'Connections', text: 'Always reached ${always.join(', ')}', value: 100, unit: '%', hours: hours));
  }
  return out;
}
