import 'dart:convert';
import 'dart:io';

import 'events.dart';
import 'fetch.dart' show Http, realHttp;
import 'report.dart';
import 'status.dart' show freshLimit;

/// Optional intelligence (roadmap row 37). Never required: everything else
/// works without it, and nothing leaves Wilson's machines.
///
/// Wilson (2026-11-27): big models on wc-alpha / wc-beta when they are up,
/// small ones on demand on the device in use. So:
/// * any computer with a local Ollama (127.0.0.1:11434) publishes reading
///   `ai.models` (its model names);
/// * a request is a file `ai/requests/<id>.json`
///   (`{"id", "kind", "from", "t", "text"?, "small"?}`); kinds:
///   `week-summary` (the last 7 days of events, in plain words) and
///   `explain` (`text` = a compatibility verdict or any finding to explain);
/// * the answering computer is the requester itself when it asked for
///   `small` and has a model, else the first fresh computer with models in
///   `model/ai.json` `hosts` order (default: wc-alpha, then wc-beta, by id),
///   else any fresh computer with models;
/// * the answer is `ai/answers/<id>.json` (`{"id", "by", "model", "t",
///   "text"}` or `"error"`).
const ollama = 'http://127.0.0.1:11434';
const defaultAiHosts = ['d8fea3c0a082', 'ebad35097c54']; // wc-alpha, wc-beta

/// Model names a local Ollama has, or null when none answers.
Future<List<String>?> localModels({Http http = realHttp}) async {
  try {
    final (code, body) = await http('GET', Uri.parse('$ollama/api/tags'), const {}, null);
    if (code != 200) return null;
    return [for (final m in ((jsonDecode(body) as Map)['models'] as List? ?? const [])) if (m is Map && m['name'] is String) m['name'] as String];
  } catch (_) {
    return null;
  }
}

List<String> modelsOf(Report r) {
  final v = r.readings.where((x) => x.key == 'ai.models').firstOrNull?.value;
  return v is String && v.isNotEmpty ? v.split(', ') : const [];
}

/// The computer that answers requests that don't ask for `small`.
String? aiHost(String root, Map<String, Report> reports, DateTime now) {
  bool ok(Report r) => r.kind != 'android' && !r.isThing && now.difference(r.time) <= freshLimit(r) && modelsOf(r).isNotEmpty;
  var order = defaultAiHosts;
  try {
    final j = jsonDecode(File('$root/model/ai.json').readAsStringSync());
    if (j is Map && j['hosts'] is List) order = [for (final h in j['hosts'] as List) '$h'];
  } catch (_) {}
  for (final id in order) {
    final r = reports[id];
    if (r != null && ok(r)) return id;
  }
  final any = reports.values.where(ok).map((r) => r.id).toList()..sort();
  return any.firstOrNull;
}

/// The biggest model by the size in its name ("llama3.1:70b" > "qwen2.5:3b"),
/// or the smallest when [small].
String pickModel(List<String> models, {bool small = false}) {
  double size(String m) => double.tryParse(RegExp(r'(\d+(?:\.\d+)?)b\b').firstMatch(m.toLowerCase())?.group(1) ?? '') ?? 7;
  final s = [...models]..sort((a, b) => size(a).compareTo(size(b)));
  return small ? s.first : s.last;
}

String weekSummaryPrompt(List<Event> events, Map<String, String> names, DateTime now) {
  final lines = [
    for (final e in events.take(400))
      '${e.time.toIso8601String().substring(0, 16)} ${names[e.device] ?? e.device ?? '?'}: ${e.type}${e.name != null ? ' ${e.name}' : ''}${e.detail != null ? ' (${e.detail})' : ''}',
  ];
  return 'You summarise a home network monitor\'s event log for its owner. '
      'Write at most 8 short plain-English sentences: what went wrong, what recovered, anything that repeats, '
      'and anything worth doing. No jargon, no invented facts; if nothing notable happened, say so.\n\n'
      'Events from the last 7 days (UTC):\n${lines.isEmpty ? '(none)' : lines.join('\n')}';
}

String explainPrompt(String text) => 'Explain to a non-expert, in at most 5 short sentences, what this finding from a '
    'home equipment checker means and what they could do about it. Do not invent facts beyond it.\n\n$text';

/// Answers the requests this computer should answer. Returns log lines.
Future<List<String>> answerAiRequests(String root, String me, Map<String, Report> reports, List<String> myModels, DateTime now, {Http http = realHttp}) async {
  final log = <String>[];
  if (myModels.isEmpty) return log;
  final req = Directory('$root/ai/requests');
  if (!req.existsSync()) return log;
  final host = aiHost(root, reports, now);
  final names = {for (final r in reports.values) r.id: r.name};
  for (final f in req.listSync().whereType<File>().where((f) => f.path.endsWith('.json'))) {
    Map j;
    try {
      j = jsonDecode(f.readAsStringSync()) as Map;
    } catch (_) {
      continue;
    }
    final id = '${j['id'] ?? ''}';
    if (!RegExp(r'^[A-Za-z0-9_-]{1,64}$').hasMatch(id)) continue;
    final out = File('$root/ai/answers/$id.json');
    if (out.existsSync()) continue;
    final small = j['small'] == true;
    final from = reports['${j['from']}'];
    final selfServes = small && from != null && modelsOf(from).isNotEmpty;
    final mine = small && j['from'] == me ? true : (host == me && !selfServes);
    if (!mine) continue;
    final asked = j['t'] is num ? DateTime.fromMillisecondsSinceEpoch((j['t'] as num).toInt() * 1000, isUtc: true) : now;
    if (now.difference(asked) > const Duration(days: 2)) continue;
    final prompt = switch (j['kind']) {
      'week-summary' => weekSummaryPrompt(readEvents(root, now.subtract(const Duration(days: 7)), now), names, now),
      'explain' when j['text'] is String => explainPrompt((j['text'] as String).length > 4000 ? (j['text'] as String).substring(0, 4000) : j['text'] as String),
      _ => null,
    };
    final model = pickModel(myModels, small: small);
    final answer = <String, Object?>{'id': id, 'by': me, 'model': model, 't': now.millisecondsSinceEpoch ~/ 1000};
    if (prompt == null) {
      answer['error'] = 'unknown kind ${j['kind']}';
    } else {
      try {
        final (code, body) = await http('POST', Uri.parse('$ollama/api/generate'), {'Content-Type': 'application/json'},
            jsonEncode({'model': model, 'prompt': prompt, 'stream': false}));
        if (code != 200) throw StateError('Ollama answered $code');
        answer['text'] = ((jsonDecode(body) as Map)['response'] as String? ?? '').trim();
      } catch (e) {
        answer['error'] = '$e';
      }
    }
    out.parent.createSync(recursive: true);
    final tmp = File('${out.path}.tmp');
    tmp.writeAsStringSync(jsonEncode(answer), flush: true);
    tmp.renameSync(out.path);
    log.add('ai: answered ${j['kind']} $id with $model${answer['error'] != null ? ' (error: ${answer['error']})' : ''}');
  }
  return log;
}

/// Writes a request (for the app). [small] asks the device in use.
String requestAi(String root, String me, String kind, {String? text, bool small = false, DateTime? now}) {
  final t = now ?? DateTime.now().toUtc();
  final id = '${me}_${t.millisecondsSinceEpoch}';
  final f = File('$root/ai/requests/$id.json');
  f.parent.createSync(recursive: true);
  f.writeAsStringSync(jsonEncode({'id': id, 'kind': kind, 'from': me, 't': t.millisecondsSinceEpoch ~/ 1000, if (text != null) 'text': text, if (small) 'small': true}));
  return id;
}

/// The answer, once written.
Map<String, Object?>? aiAnswer(String root, String id) {
  try {
    return (jsonDecode(File('$root/ai/answers/$id.json').readAsStringSync()) as Map).cast<String, Object?>();
  } catch (_) {
    return null;
  }
}
