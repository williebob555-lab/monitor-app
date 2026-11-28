import 'dart:convert';
import 'dart:io';

import 'bom.dart';
import 'compat.dart';
import 'fetch.dart';
import 'library.dart';
import 'model.dart';
import 'project.dart';

/// Discovery (roadmap row 33): for a cable or adapter a project is missing,
/// search the shops and give each listing a compatibility verdict.
///
/// A listing says nothing machine-readable about its ends, so the two
/// connectors are read from its title ("USB-C to USB-A cable", "Cat6
/// Ethernet patch cable") and the chain is checked with what those shapes
/// may carry: verdicts are at best `estimated`, and a title that names no
/// known connector is `unknown`, never `ok`.
///
/// Asked for with a file `discover/requests/<id>.json`
/// (`{"id", "project", "from", "t"}`), answered by the computer in
/// `model/fetch.json` as `discover/answers/<id>.json`:
/// `{"id", "by", "t", "project", "needs": [{"name", "why", "query",
/// "candidates": [{"title", "source", "seller", "price", "currency", "url",
/// "a", "b", "verdict", "evidence", "reasons"}]}]}` or `"error"`.

/// Words in listing titles -> connector ids, most specific first.
const _words = <(String, String)>[
  (r'mini[ -]?displayport|mini[ -]?dp\b', 'mini-displayport'),
  (r'displayport|\bdp\b', 'displayport'),
  (r'mini[ -]?hdmi', 'hdmi-c'),
  (r'micro[ -]?hdmi', 'hdmi-d'),
  (r'hdmi', 'hdmi-a'),
  (r'micro[ -]?usb', 'micro-usb-b'),
  (r'mini[ -]?usb', 'mini-usb-b'),
  (r'usb[ -]?c\b|type[ -]?c\b|usb[ -]?type[ -]?c', 'usb-c'),
  (r'usb[ -]?b\b|type[ -]?b\b|printer cable', 'usb-b'),
  (r'usb[ -]?a\b|type[ -]?a\b|usb[ -]?3\.0 a\b', 'usb-a'),
  (r'lightning', 'lightning'),
  (r'rj[ -]?45|ethernet|cat ?5e?\b|cat ?6a?\b|cat ?7\b|cat ?8\b|patch cable', 'rj45'),
  (r'\bsfp\+', 'sfp-plus'),
  (r'\blc\b', 'lc-duplex'),
  (r'\bc13\b', 'iec-c13'),
  (r'\bc14\b', 'iec-c14'),
  (r'\bc19\b|\bc20\b', 'iec-c19'),
  (r'\bc7\b|figure[ -]?8', 'iec-c7'),
  (r'\bc5\b|cloverleaf', 'iec-c5'),
  (r'nema ?5-15|5-15p', 'nema-5-15'),
  (r'dvi', 'dvi-d-dl'),
  (r'\bvga\b', 'vga'),
  (r'xlr', 'xlr3'),
  (r'1/4"? ?trs|6\.35 ?mm trs|quarter[ -]inch trs', 'trs-635'),
  (r'1/4"? ?ts|6\.35 ?mm ts|1/4"|6\.35 ?mm|instrument cable|guitar cable', 'ts-635'),
  (r'3\.5 ?mm|aux cable', 'trs-35'),
  (r'\brca\b', 'rca'),
  (r'sata power', 'sata-power'),
];

/// Short words to search with, per connector id.
const _searchWord = {
  'usb-a': 'USB-A', 'usb-a-ss': 'USB-A', 'usb-b': 'USB-B', 'usb-b3': 'USB 3 B', 'usb-c': 'USB-C', 'micro-usb-b': 'Micro USB',
  'mini-usb-b': 'Mini USB', 'hdmi-a': 'HDMI', 'hdmi-c': 'Mini HDMI', 'hdmi-d': 'Micro HDMI', 'displayport': 'DisplayPort',
  'mini-displayport': 'Mini DisplayPort', 'rj45': 'Ethernet', 'ethercon': 'Ethernet', 'iec-c13': 'C13', 'iec-c14': 'C14',
  'nema-5-15': 'NEMA 5-15P', 'xlr3': 'XLR', 'trs-635': '1/4" TRS', 'ts-635': '1/4" TS', 'trs-35': '3.5mm', 'rca': 'RCA',
  'lc-duplex': 'LC LC fiber', 'vga': 'VGA', 'dvi-d-dl': 'DVI',
};

/// The connectors a listing title names, in the order it names them.
List<String> connectorsInTitle(String title) {
  final t = title.toLowerCase();
  final hits = <(int, int, String)>[];
  for (final (re, id) in _words) {
    for (final m in RegExp(re).allMatches(t)) {
      if (hits.any((h) => m.start < h.$2 && m.end > h.$1)) continue;
      hits.add((m.start, m.end, id));
    }
  }
  hits.sort((a, b) => a.$1.compareTo(b.$1));
  final out = <String>[];
  for (final h in hits) {
    if (out.isEmpty || out.last != h.$3) out.add(h.$3);
  }
  return out;
}

/// What to type into a shop's search for a missing part.
String queryForNeed(BomLine need) {
  final a = need.from?.connector, b = need.to?.connector;
  String w(String? c) => c == null ? '' : (_searchWord[c] ?? c);
  if (a == null || b == null) return need.name;
  if (a == 'rj45' && b == 'rj45') return 'Cat6 Ethernet patch cable';
  return w(a) == w(b) ? '${w(a)} cable' : '${w(a)} to ${w(b)} cable';
}

class Candidate {
  Candidate(this.offer, this.a, this.b, this.verdict, this.evidence, this.reasons);
  final FetchedOffer offer;
  final String? a, b;
  final String verdict, evidence;
  final List<String> reasons;

  Map<String, Object?> toJson() => {
        'title': offer.title,
        'source': offer.source,
        'seller': offer.offer.seller,
        if (offer.offer.price != null) 'price': offer.offer.price,
        if (offer.offer.currency != null) 'currency': offer.offer.currency,
        if (offer.offer.url != null) 'url': offer.offer.url,
        if (a != null) 'a': a,
        if (b != null) 'b': b,
        'verdict': verdict,
        'evidence': evidence,
        'reasons': reasons,
      };
}

/// One listing against the two ports it must join.
Candidate judge(FetchedOffer o, Port from, Port to, {String? want, Library? library}) {
  final ends = connectorsInTitle(o.title ?? '');
  if (ends.isEmpty) return Candidate(o, null, null, 'unknown', 'unknown', ['the listing title names no connector']);
  final a = ends.first, b = ends.length > 1 ? ends[1] : ends.first;
  ChainVerdict check(String x, String y) => checkChain(from, [Link(o.title ?? 'listing', x, y)], to, want: want, library: library);
  // The title may name the ends in either order.
  final fwd = check(a, b), rev = a == b ? fwd : check(b, a);
  int rank(ChainVerdict v) => switch (v.verdict) { 'ok' => 0, 'unknown' => 1, _ => 2 };
  final (v, x, y) = rank(rev) < rank(fwd) ? (rev, b, a) : (fwd, a, b);
  final reasons = [for (final l in v.layers) if (l.verdict != 'ok') ...l.reasons];
  // Read from a title, so never better than estimated.
  final evidence = v.layers.any((l) => l.evidence == 'unknown') ? 'unknown' : 'estimated';
  return Candidate(o, x, y, v.verdict, evidence, reasons.isEmpty ? ['ends read from the title: $x to $y'] : reasons);
}

/// Searches every usable source for [need] and returns up to [limit]
/// candidates that are not known to fail, best verdict and then cheapest
/// first.
Future<List<Candidate>> discover(BomLine need, List<OfferSource> sources, {Http http = realHttp, Library? library, int limit = 3}) async {
  if (need.from == null || need.to == null) return const [];
  final q = queryForNeed(need);
  final found = <Candidate>[];
  for (final s in sources.where((s) => s.missing == null)) {
    for (final o in await s.search(q, http)) {
      found.add(judge(o, need.from!, need.to!, want: need.want, library: library));
    }
  }
  int rank(Candidate c) => switch (c.verdict) { 'ok' => 0, 'unknown' => 1, _ => 2 };
  final ok = found.where((c) => c.verdict != 'fail').toList()
    ..sort((x, y) {
      final r = rank(x).compareTo(rank(y));
      return r != 0 ? r : (x.offer.offer.price ?? double.infinity).compareTo(y.offer.offer.price ?? double.infinity);
    });
  return ok.take(limit).toList();
}

/// Answers `discover/requests` (only on the fetching computer). Returns log
/// lines.
Future<List<String>> answerDiscoverRequests(String root, String me, List<OfferSource> sources, DateTime now, {Http http = realHttp}) async {
  final log = <String>[];
  final req = Directory('$root/discover/requests');
  if (!req.existsSync()) return log;
  for (final f in req.listSync().whereType<File>().where((f) => f.path.endsWith('.json'))) {
    Map j;
    try {
      j = jsonDecode(f.readAsStringSync()) as Map;
    } catch (_) {
      continue;
    }
    final id = '${j['id'] ?? ''}';
    if (!RegExp(r'^[A-Za-z0-9_-]{1,64}$').hasMatch(id)) continue;
    final out = File('$root/discover/answers/$id.json');
    if (out.existsSync()) continue;
    final asked = j['t'] is num ? DateTime.fromMillisecondsSinceEpoch((j['t'] as num).toInt() * 1000, isUtc: true) : now;
    if (now.difference(asked) > const Duration(days: 2)) continue;
    final answer = <String, Object?>{'id': id, 'by': me, 't': now.millisecondsSinceEpoch ~/ 1000, 'project': j['project']};
    final project = ProjectStore(root, me).get('${j['project']}');
    if (project == null) {
      answer['error'] = 'no project ${j['project']}';
    } else if (sources.every((s) => s.missing != null)) {
      answer['error'] = 'no shop can be searched: ${sources.map((s) => s.missing).join('; ')}';
    } else {
      final items = <String, Item>{for (final it in ModelStore(root, me).list(withRetired: true)) it.id: it};
      final needs = [];
      for (final l in projectNeeds(project, items).lines.where((l) => l.status == 'missing' && l.from != null)) {
        try {
          final c = await discover(l, sources, http: http);
          needs.add({'name': l.name, 'why': l.why, 'query': queryForNeed(l), 'candidates': [for (final x in c) x.toJson()]});
        } catch (e) {
          needs.add({'name': l.name, 'why': l.why, 'query': queryForNeed(l), 'error': '$e'});
        }
      }
      answer['needs'] = needs;
    }
    out.parent.createSync(recursive: true);
    final tmp = File('${out.path}.tmp')..writeAsStringSync(jsonEncode(answer), flush: true);
    tmp.renameSync(out.path);
    final n = (answer['needs'] as List?)?.length ?? 0;
    log.add('discover: ${j['project']}: ${answer['error'] ?? '$n missing part${n == 1 ? '' : 's'} searched'}');
  }
  return log;
}

/// Writes a request (for the app's "find me one").
String requestDiscover(String root, String me, String projectId, {DateTime? now}) {
  final t = now ?? DateTime.now().toUtc();
  final id = '${me}_${t.millisecondsSinceEpoch}';
  final f = File('$root/discover/requests/$id.json');
  f.parent.createSync(recursive: true);
  f.writeAsStringSync(jsonEncode({'id': id, 'project': projectId, 'from': me, 't': t.millisecondsSinceEpoch ~/ 1000}));
  return id;
}

/// The answer, once written.
Map<String, Object?>? discoverAnswer(String root, String id) {
  try {
    return (jsonDecode(File('$root/discover/answers/$id.json').readAsStringSync()) as Map).cast<String, Object?>();
  } catch (_) {
    return null;
  }
}
