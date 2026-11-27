import 'dart:convert';
import 'dart:io';

import 'history.dart' show readLines;
import 'rules.dart';

/// Notifications from rules (roadmap row 20): every device reads the firing
/// logs (`rules/<id>/fired/*.jsonl`) and shows each new firing that has a
/// `notify` once, if it is meant for it (`on` missing or naming it).
/// Firings that reach a device more than [maxAge] late (it was off, or
/// Syncthing was behind) are not shown: an old alarm is noise.
class Notice {
  Notice(this.key, this.title, this.text, this.time);

  /// rule|subject|since: the same firing has the same key everywhere.
  final String key;
  final String title, text;
  final DateTime time;
}

class NotifyInbox {
  NotifyInbox(this.statePath) {
    try {
      final j = jsonDecode(File(statePath).readAsStringSync()) as Map;
      (j['shown'] as Map? ?? {}).forEach((k, v) => _shown['$k'] = (v as num).toInt());
      if (j['from'] is num) _from = (j['from'] as num).toInt();
    } catch (_) {}
  }

  static const maxAge = Duration(hours: 1);
  final String statePath;
  final _shown = <String, int>{};
  int? _from;

  /// New notices for [me]. The first call on a device only marks where it
  /// starts, so installing never replays old alarms.
  List<Notice> take(String root, String me, DateTime now) {
    final nowS = now.millisecondsSinceEpoch ~/ 1000;
    if (_from == null) {
      _from = nowS;
      _save();
      return const [];
    }
    final oldest = [nowS - maxAge.inSeconds, _from!].reduce((a, b) => a > b ? a : b);
    final out = <Notice>[];
    final d = Directory(RuleSet.dir(root));
    if (d.existsSync()) {
      for (final rd in d.listSync().whereType<Directory>()) {
        final fd = Directory('${rd.path}${Platform.pathSeparator}fired');
        if (!fd.existsSync()) continue;
        for (final f in fd.listSync().whereType<File>()) {
          if (!f.path.endsWith('.jsonl') || f.path.contains('.sync-conflict-')) continue;
          // Unchanged since before the window: nothing new in it.
          if (f.lastModifiedSync().millisecondsSinceEpoch ~/ 1000 < oldest - 300) continue;
          for (final l in readLines(f)) {
            final t = (l['t'] as num?)?.toInt() ?? 0;
            final text = l['notify'];
            if (t < oldest || t > nowS + 300 || text is! String) continue;
            final on = l['on'];
            if (on is List && !on.contains(me)) continue;
            final key = '${l['rule']}|${l['subject']}|${l['since']}';
            if (_shown.containsKey(key)) continue;
            _shown[key] = t;
            out.add(Notice(key, '${l['name'] ?? l['rule']}', text, DateTime.fromMillisecondsSinceEpoch(t * 1000, isUtc: true)));
          }
        }
      }
    }
    _shown.removeWhere((_, t) => t < nowS - 2 * maxAge.inSeconds);
    if (out.isNotEmpty) _save();
    out.sort((a, b) => a.time.compareTo(b.time));
    return out;
  }

  void _save() {
    try {
      final tmp = File('$statePath.tmp');
      tmp.writeAsStringSync(jsonEncode({'from': _from, 'shown': _shown}));
      tmp.renameSync(statePath);
    } catch (_) {}
  }
}
