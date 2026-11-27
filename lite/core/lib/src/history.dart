import 'dart:convert';
import 'dart:io';
import 'dart:math' as math;

import 'report.dart' show fieldAt;

/// History that stays small and cheap to sync.
///
///   devices/<id>/history/detail/YYYY-MM-DD-HH.jsonl  every reading, one file per hour
///   devices/<id>/history/hourly/YYYY-MM.jsonl        one summary line per hour
///   devices/<id>/history/daily/YYYY.jsonl            one summary line per day
///   devices/<id>/history/patterns/YYYY.jsonl         one line of pattern values per day
///
/// Why this shape:
/// * Readings first go to a local spool file that is NOT synced. Every
///   15 minutes they move into the hour's detail file. So Syncthing sees one
///   small change per 15 minutes (the hour file never passes ~15 KB), not a
///   growing file rewritten every minute.
/// * When an hour ends, the device summarises it (min/avg/max per value) into
///   the month's hourly file; when a day ends, into the year's daily file.
/// * Old files are deleted by age (see [Retention], shared in settings.json)
///   and a hard size cap per device deletes oldest-first if ever exceeded.
/// * Readers open at most ~25 detail files (24 h), one or two hourly files
///   (7-30 days) or the daily files, so reading never slows as history grows.
///
/// The Android collector (Collector.kt) implements the same rules.
class Retention {
  const Retention({this.detailDays = 7, this.hourlyDays = 365, this.dailyDays = 1825, this.maxMb = 50});
  final int detailDays, hourlyDays, dailyDays, maxMb;

  static const defaults = Retention();

  static Retention fromJson(Object? j) {
    int pick(String k, int d, int lo, int hi) {
      final v = j is Map ? j[k] : null;
      return v is num ? v.round().clamp(lo, hi) : d;
    }

    return Retention(
      detailDays: pick('detailDays', defaults.detailDays, 1, 90),
      hourlyDays: pick('hourlyDays', defaults.hourlyDays, 7, 3650),
      dailyDays: pick('dailyDays', defaults.dailyDays, 30, 36500),
      maxMb: pick('maxMb', defaults.maxMb, 5, 2000),
    );
  }

  Map<String, Object?> toJson() =>
      {'detailDays': detailDays, 'hourlyDays': hourlyDays, 'dailyDays': dailyDays, 'maxMb': maxMb};

  /// Shared settings live in <folder>/settings.json so every device applies
  /// the same limits. Missing or unreadable means defaults.
  static Retention load(String folder) {
    try {
      final j = jsonDecode(File('$folder${Platform.pathSeparator}settings.json').readAsStringSync());
      return Retention.fromJson(j is Map ? j['history'] : null);
    } catch (_) {
      return defaults;
    }
  }

  void save(String folder) {
    final f = File('$folder${Platform.pathSeparator}settings.json');
    Map<String, Object?> all = {};
    try {
      final j = jsonDecode(f.readAsStringSync());
      if (j is Map<String, Object?>) all = j;
    } catch (_) {}
    all['history'] = toJson();
    final tmp = File('$folder${Platform.pathSeparator}.syncthing.settings.json.tmp');
    tmp.writeAsStringSync(const JsonEncoder.withIndent('  ').convert(all));
    tmp.renameSync(f.path);
  }
}

String _two(int v) => v.toString().padLeft(2, '0');
String detailName(DateTime h) => '${h.year}-${_two(h.month)}-${_two(h.day)}-${_two(h.hour)}.jsonl';
String hourlyName(DateTime d) => '${d.year}-${_two(d.month)}.jsonl';
String dailyName(DateTime d) => '${d.year}.jsonl';

/// Summarises readings into one line: {"t": start, "n": count, key: [min, avg, max]}.
/// Every numeric value is summarised, including nested ones like p.<peer>
/// (stored as "p.<peer>"), so new readings need no code change.
Map<String, Object?> summarize(List<Map<String, Object?>> lines, int start) {
  final acc = <String, List<double>>{};
  void add(String k, Object? v) {
    if (v is num) {
      final a = acc.putIfAbsent(k, () => [double.infinity, 0, double.negativeInfinity, 0]);
      final d = v.toDouble();
      a[0] = math.min(a[0], d);
      a[1] += d;
      a[2] = math.max(a[2], d);
      a[3] += 1;
    } else if (v is List && v.length == 3 && v.every((x) => x is num)) {
      // Summaries of summaries: min of mins, mean of means, max of maxes.
      final a = acc.putIfAbsent(k, () => [double.infinity, 0, double.negativeInfinity, 0]);
      a[0] = math.min(a[0], (v[0] as num).toDouble());
      a[1] += (v[1] as num).toDouble();
      a[2] = math.max(a[2], (v[2] as num).toDouble());
      a[3] += 1;
    } else if (v is Map) {
      v.forEach((kk, vv) => add('$k.$kk', vv));
    }
  }

  var n = 0;
  for (final l in lines) {
    n += (l['n'] as num?)?.toInt() ?? 1;
    l.forEach((k, v) {
      if (k != 't' && k != 'n') add(k, v);
    });
  }
  double r1(double v) => (v * 10).roundToDouble() / 10;
  return {
    't': start,
    'n': n,
    for (final e in acc.entries) e.key: [r1(e.value[0]), r1(e.value[1] / e.value[3]), r1(e.value[2])],
  };
}

List<Map<String, Object?>> readLines(File f) {
  if (!f.existsSync()) return const [];
  final out = <Map<String, Object?>>[];
  for (final line in const LineSplitter().convert(f.readAsStringSync())) {
    try {
      final j = jsonDecode(line);
      if (j is Map<String, Object?> && j['t'] is num) out.add(j);
    } catch (_) {} // a half-synced last line is skipped
  }
  return out;
}

/// The writer side: owned by one device for its own history.
class HistoryWriter {
  HistoryWriter(this.deviceDir, this.spoolDir);
  final String deviceDir;

  /// Local, never synced (app data / config folder).
  final String spoolDir;

  static const flushEvery = Duration(minutes: 15);
  String get _sep => Platform.pathSeparator;
  String get historyDir => '$deviceDir${_sep}history';
  File get _spool => File('$spoolDir${_sep}history-spool.jsonl');
  File get _state => File('$spoolDir${_sep}history-state.json');

  Map<String, Object?> _readState() {
    try {
      final j = jsonDecode(_state.readAsStringSync());
      if (j is Map<String, Object?>) return j;
    } catch (_) {}
    return {};
  }

  void _writeState(Map<String, Object?> s) => _state.writeAsStringSync(jsonEncode(s));

  /// Record one reading line (must have "t" in Unix seconds). Flushes to the
  /// synced folder when due.
  void add(Map<String, Object?> line, DateTime now) {
    Directory(spoolDir).createSync(recursive: true);
    _spool.writeAsStringSync('${jsonEncode(line)}\n', mode: FileMode.append, flush: true);
    final st = _readState();
    final last = (st['lastFlush'] as num?)?.toInt() ?? 0;
    final nowS = now.millisecondsSinceEpoch ~/ 1000;
    final firstOfHour = last ~/ 3600 != nowS ~/ 3600;
    if (nowS - last >= flushEvery.inSeconds || firstOfHour) flush(now);
  }

  /// Move spooled readings into their hour files, then write summaries for
  /// hours and days that have ended.
  void flush(DateTime now) {
    final lines = readLines(_spool);
    final byHour = <String, List<Map<String, Object?>>>{};
    for (final l in lines) {
      final t = DateTime.fromMillisecondsSinceEpoch((l['t']! as num).toInt() * 1000);
      byHour.putIfAbsent(detailName(t), () => []).add(l);
    }
    final detail = Directory('$historyDir${_sep}detail')..createSync(recursive: true);
    byHour.forEach((name, ls) {
      File('${detail.path}$_sep$name')
          .writeAsStringSync(ls.map((l) => '${jsonEncode(l)}\n').join(), mode: FileMode.append, flush: true);
    });
    if (_spool.existsSync()) _spool.writeAsStringSync('');
    final st = _readState();
    st['lastFlush'] = now.millisecondsSinceEpoch ~/ 1000;
    _summarizeEnded(now, st);
    _writeState(st);
  }

  void _summarizeEnded(DateTime now, Map<String, Object?> st) {
    final local = now.toLocal();
    final thisHour = DateTime(local.year, local.month, local.day, local.hour);
    var h = st['hourlyDone'] is num
        ? DateTime.fromMillisecondsSinceEpoch((st['hourlyDone']! as num).toInt() * 1000).add(const Duration(hours: 1))
        : thisHour.subtract(const Duration(hours: 1));
    // Catch up at most 2 days of missed hours (device was off longer: those
    // hours have no detail anyway).
    if (thisHour.difference(h) > const Duration(days: 2)) h = thisHour.subtract(const Duration(days: 2));
    final hourlyDir = Directory('$historyDir${_sep}hourly')..createSync(recursive: true);
    while (h.isBefore(thisHour)) {
      final ls = readLines(File('$historyDir${_sep}detail$_sep${detailName(h)}'));
      if (ls.isNotEmpty) {
        File('${hourlyDir.path}$_sep${hourlyName(h)}').writeAsStringSync(
            '${jsonEncode(summarize(ls, h.millisecondsSinceEpoch ~/ 1000))}\n',
            mode: FileMode.append, flush: true);
      }
      st['hourlyDone'] = h.millisecondsSinceEpoch ~/ 1000;
      h = h.add(const Duration(hours: 1));
    }
    final today = DateTime(local.year, local.month, local.day);
    var d = st['dailyDone'] is num
        ? DateTime.fromMillisecondsSinceEpoch((st['dailyDone']! as num).toInt() * 1000).add(const Duration(days: 1))
        : today.subtract(const Duration(days: 1));
    if (today.difference(d) > const Duration(days: 7)) d = today.subtract(const Duration(days: 7));
    final dailyDir = Directory('$historyDir${_sep}daily')..createSync(recursive: true);
    while (d.isBefore(today)) {
      final start = d.millisecondsSinceEpoch ~/ 1000;
      final end = DateTime(d.year, d.month, d.day + 1).millisecondsSinceEpoch ~/ 1000;
      final ls = readLines(File('${hourlyDir.path}$_sep${hourlyName(d)}'))
          .where((l) => (l['t']! as num) >= start && (l['t']! as num) < end)
          .toList();
      if (ls.isNotEmpty) {
        File('${dailyDir.path}$_sep${dailyName(d)}')
            .writeAsStringSync('${jsonEncode(summarize(ls, start))}\n', mode: FileMode.append, flush: true);
      }
      st['dailyDone'] = start;
      d = DateTime(d.year, d.month, d.day + 1);
    }
  }

  /// Delete by age, then enforce the size cap oldest-first. Returns bytes used.
  int prune(DateTime now, Retention r) {
    final files = <File>[];
    final local = now.toLocal();
    bool older(String name, DateTime limit, String kind) {
      final p = name.replaceAll('.jsonl', '').split('-').map(int.tryParse).toList();
      if (p.any((x) => x == null) || p.isEmpty) return false;
      final DateTime end = switch (kind) {
        'detail' when p.length == 4 => DateTime(p[0]!, p[1]!, p[2]!, p[3]! + 1),
        'hourly' when p.length == 2 => DateTime(p[0]!, p[1]! + 1),
        'daily' || 'patterns' when p.length == 1 => DateTime(p[0]! + 1),
        _ => local,
      };
      return end.isBefore(limit);
    }

    for (final (kind, days) in [('detail', r.detailDays), ('hourly', r.hourlyDays), ('daily', r.dailyDays), ('patterns', r.dailyDays)]) {
      final dir = Directory('$historyDir$_sep$kind');
      if (!dir.existsSync()) continue;
      for (final e in dir.listSync().whereType<File>()) {
        final name = e.uri.pathSegments.last;
        if (name.contains('.sync-conflict-') || older(name, local.subtract(Duration(days: days)), kind)) {
          e.deleteSync();
        } else {
          files.add(e);
        }
      }
    }
    var total = files.fold<int>(0, (a, f) => a + f.lengthSync());
    final cap = r.maxMb * 1024 * 1024;
    if (total > cap) {
      // Oldest detail first, then oldest hourly; daily summaries go last.
      int rank(File f) => f.path.contains('${_sep}detail$_sep') ? 0 : (f.path.contains('${_sep}hourly$_sep') ? 1 : 2);
      files.sort((a, b) {
        final c = rank(a).compareTo(rank(b));
        return c != 0 ? c : a.path.compareTo(b.path);
      });
      for (final f in files) {
        if (total <= cap) break;
        total -= f.lengthSync();
        f.deleteSync();
      }
    }
    return total;
  }
}

/// The reader side, for the app. Picks the coarsest files that cover the
/// range, so a year view reads one or two small files.
enum HistoryRange { day, week, year }

List<Map<String, Object?>> readHistoryRange(String deviceDir, HistoryRange range, DateTime now) {
  final sep = Platform.pathSeparator;
  final h = '$deviceDir${sep}history';
  final local = now.toLocal();
  final from = now.millisecondsSinceEpoch ~/ 1000 - switch (range) {
        HistoryRange.day => 86400,
        HistoryRange.week => 7 * 86400,
        HistoryRange.year => 365 * 86400,
      };
  final out = <Map<String, Object?>>[];
  switch (range) {
    case HistoryRange.day:
      for (var i = 24; i >= 0; i--) {
        out.addAll(readLines(File('$h${sep}detail$sep${detailName(local.subtract(Duration(hours: i)))}')));
      }
    case HistoryRange.week:
      final months = {hourlyName(local.subtract(const Duration(days: 7))), hourlyName(local)};
      for (final m in months) {
        out.addAll(readLines(File('$h${sep}hourly$sep$m')));
      }
    case HistoryRange.year:
      for (final y in {dailyName(local.subtract(const Duration(days: 365))), dailyName(local)}) {
        out.addAll(readLines(File('$h${sep}daily$sep$y')));
      }
  }
  out.removeWhere((l) => (l['t']! as num) < from);
  out.sort((a, b) => (a['t']! as num).compareTo(b['t']! as num));
  return out;
}

/// A chartable number from a detail line (plain value) or a summary line
/// ([min, avg, max] -> avg).
double? chartValue(Map<String, Object?> line, String path) {
  final v = fieldAt(line, path) ?? line[path];
  if (v is num) return v.toDouble();
  if (v is List && v.length == 3 && v[1] is num) return (v[1] as num).toDouble();
  return null;
}

/// Bytes used by one device's history (for the Setup page).
int historyBytes(String deviceDir) {
  final d = Directory('$deviceDir${Platform.pathSeparator}history');
  if (!d.existsSync()) return 0;
  return d.listSync(recursive: true).whereType<File>().fold(0, (a, f) => a + f.lengthSync());
}
