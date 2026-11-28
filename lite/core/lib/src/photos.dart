import 'dart:convert';
import 'dart:io';
import 'dart:math' as math;
import 'dart:typed_data';

import 'package:image/image.dart' as img;

import 'fetch.dart';
import 'products.dart';

/// Exact-model front photos (roadmap row 31a, #91). For a product with a
/// maker and an exact model, find a straight-on front view on the maker's
/// own site, crop it to the chassis (ears included), and keep where it came
/// from, so rack faceplates show the real product, offline.
///
/// Permitted sources only, found with no manual step:
/// 1. pages and images the product record names (`docs` urls, `images`);
/// 2. the maker's site: its robots.txt `Sitemap:` lines lead to the pages
///    whose address names the model (`/products/828/`, `...cp1500pfcrm2u`).
///    robots.txt is obeyed for every page and image fetched.
///
/// A page counts as the exact model only when its text names the model as a
/// whole word (an "828es" page is not an "828"), and its revision ("(2024)",
/// or the product's `revision`) when there is one; otherwise the photo is
/// kept for review, never shown as confirmed. A crop whose width/height
/// does not fit the product's rack units (19 in wide, 1.75 in per U) is
/// also left for review.
///
/// Result, per product: `model/products/<id>/front.jpg` and `front.json`
/// `{"status": "confirmed"|"review"|"none", "model", "revision", "page",
/// "image", "crop": [x, y, w, h], "size": [w, h], "ratio", "reasons", "t",
/// "by"}`. Kept until the product's maker, model or revision change.

/// Known makers' sites; `model/photos.json` `{"makers": {"name": "site"}}`
/// adds or overrides.
const makerSites = {
  'motu': 'motu.com',
  'cyberpower': 'cyberpowersystems.com',
  'apc': 'apc.com',
  'ubiquiti': 'ui.com',
  'netgear': 'netgear.com',
  'tp-link': 'tp-link.com',
  'focusrite': 'focusrite.com',
  'behringer': 'behringer.com',
  'tripp lite': 'tripplite.com',
  'eaton': 'eaton.com',
};

String _norm(String s) => s.toLowerCase().replaceAll(RegExp(r'[^a-z0-9]+'), '');

/// "828 (2024)" -> ("828", "2024"); the product's `revision` wins.
(String, String?) modelAndRevision(Product p) {
  final m = (p.model ?? '').trim();
  final rev = RegExp(r'\(([^)]+)\)\s*$').firstMatch(m);
  final base = rev == null ? m : m.substring(0, rev.start).trim();
  final r = p.extra['revision'] is String ? p.extra['revision'] as String : rev?.group(1)?.trim();
  return (base, r);
}

/// True when [text] names [model] as a whole word ("MOTU 828 (2024)" yes,
/// "828es" no, "CP1500PFCRM2U" also as "CP1500 PFCRM2U").
bool namesModel(String text, String model) {
  if (model.isEmpty) return false;
  final parts = model.toLowerCase().split(RegExp(r'[^a-z0-9]+')).where((x) => x.isNotEmpty).map(RegExp.escape).toList();
  if (parts.isEmpty) return false;
  return RegExp('(^|[^a-z0-9])${parts.join(r'[\s\-_/]*')}(\$|[^a-z0-9])').hasMatch(text.toLowerCase());
}

/// Rules from robots.txt for user agent `*` (or ours).
class Robots {
  Robots(this.disallow, this.allow, this.sitemaps);
  final List<String> disallow, allow, sitemaps;

  static Robots parse(String text) {
    final dis = <String>[], al = <String>[], maps = <String>[];
    var applies = false, inGroup = false;
    for (final raw in const LineSplitter().convert(text)) {
      final line = raw.split('#').first.trim();
      final i = line.indexOf(':');
      if (i < 0) continue;
      final k = line.substring(0, i).trim().toLowerCase(), v = line.substring(i + 1).trim();
      if (k == 'sitemap') {
        maps.add(v);
      } else if (k == 'user-agent') {
        if (!inGroup) applies = false;
        inGroup = true;
        if (v == '*' || v.toLowerCase().startsWith('monitorapp')) applies = true;
      } else {
        inGroup = false;
        if (!applies) continue;
        if (k == 'disallow' && v.isNotEmpty) dis.add(v);
        if (k == 'allow' && v.isNotEmpty) al.add(v);
      }
    }
    return Robots(dis, al, maps);
  }

  /// Longest matching rule wins; `*` and a trailing `$` are honoured.
  bool allows(String path) {
    int best(List<String> rules) {
      var n = -1;
      for (final r in rules) {
        final re = RegExp('^${RegExp.escape(r).replaceAll(r'\*', '.*').replaceAll(r'\$', r'$')}');
        if (re.hasMatch(path)) n = math.max(n, r.length);
      }
      return n;
    }

    return best(allow) >= best(disallow);
  }
}

class PhotoResult {
  PhotoResult(this.status, this.reasons, {this.page, this.image, this.crop, this.size, this.ratio, this.jpg});
  final String status;
  final List<String> reasons;
  final String? page, image;
  final List<int>? crop, size;
  final double? ratio;
  final List<int>? jpg;
}

/// Crops to the object: the box of pixels that differ from the corners'
/// background, padded by 1 %. Null when nothing stands out.
(img.Image, List<int>)? cropToObject(img.Image im, {int tolerance = 40}) {
  final w = im.width, h = im.height;
  if (w < 16 || h < 16) return null;
  final corners = [im.getPixel(1, 1), im.getPixel(w - 2, 1), im.getPixel(1, h - 2), im.getPixel(w - 2, h - 2)];
  final bg = [for (var c = 0; c < 3; c++) corners.map((p) => [p.r, p.g, p.b][c].toDouble()).reduce((a, b) => a + b) / 4];
  var x0 = w, y0 = h, x1 = -1, y1 = -1;
  // Every second pixel is plenty for a bounding box.
  for (var y = 0; y < h; y += 2) {
    for (var x = 0; x < w; x += 2) {
      final p = im.getPixel(x, y);
      final transparent = p.a < 16 && im.numChannels == 4;
      final d = (p.r - bg[0]).abs() + (p.g - bg[1]).abs() + (p.b - bg[2]).abs();
      if (!transparent && d > tolerance) {
        x0 = math.min(x0, x);
        y0 = math.min(y0, y);
        x1 = math.max(x1, x);
        y1 = math.max(y1, y);
      }
    }
  }
  if (x1 < 0) return null;
  final px = ((x1 - x0) * 0.01).round(), py = ((y1 - y0) * 0.01).round();
  final cx = math.max(0, x0 - px), cy = math.max(0, y0 - py);
  final cw = math.min(w - cx, x1 - x0 + 2 + 2 * px), ch = math.min(h - cy, y1 - y0 + 2 + 2 * py);
  return (img.copyCrop(im, x: cx, y: cy, width: cw, height: ch), [cx, cy, cw, ch]);
}

int? _rackUnits(Product p) {
  for (final k in ['rackUnits', 'u', 'units']) {
    final v = p.specs[k]?.value ?? p.extra[k];
    if (v is num && v > 0) return v.toInt();
  }
  return null;
}

/// Images a product page offers, best first: og:image, then <img> whose
/// address or alt text says front or names the model.
List<Uri> pageImages(Uri page, String html, String model) {
  final out = <(int, Uri)>[];
  void add(String? src, int score) {
    if (src == null || src.isEmpty || src.startsWith('data:')) return;
    final u = page.resolve(src.replaceAll('&amp;', '&'));
    if (!u.isScheme('https') && !u.isScheme('http')) return;
    if (!RegExp(r'\.(jpe?g|png|webp)(\?|$)', caseSensitive: false).hasMatch(u.toString()) && score < 3) return;
    out.add((score, u));
  }

  for (final m in RegExp(r'''<meta[^>]+(?:property|name)=["'](?:og:image|twitter:image)["'][^>]*>''', caseSensitive: false).allMatches(html)) {
    add(RegExp(r'''content=["']([^"']+)''').firstMatch(m.group(0)!)?.group(1), 3);
  }
  final slug = _norm(model);
  for (final m in RegExp(r'<img\b[^>]*>', caseSensitive: false).allMatches(html)) {
    final tag = m.group(0)!;
    final src = RegExp(r'''\s(?:data-src|src)=["']([^"']+)''', caseSensitive: false).firstMatch(tag)?.group(1);
    final alt = RegExp(r'''\salt=["']([^"']*)''', caseSensitive: false).firstMatch(tag)?.group(1) ?? '';
    final hay = '${src ?? ''} $alt'.toLowerCase();
    var score = 0;
    if (hay.contains('front')) score += 2;
    if (slug.isNotEmpty && _norm(hay).contains(slug)) score += 1;
    if (RegExp(r'rear|back|side|angle|top|logo|icon').hasMatch(hay)) score -= 2;
    if (score > 0) add(src, score);
  }
  out.sort((a, b) => b.$1.compareTo(a.$1));
  final seen = <String>{};
  return [for (final (_, u) in out) if (seen.add(u.toString())) u];
}

/// Finds and crops one product's front photo.
Future<PhotoResult> findFrontPhoto(Product p, {Http http = realHttp, HttpBytes bytes = realHttpBytes, Map<String, String> sites = makerSites}) async {
  final (model, revision) = modelAndRevision(p);
  if (p.maker == null || model.isEmpty) return PhotoResult('none', ['needs a maker and an exact model']);
  final robots = <String, Robots>{};
  Future<bool> allowed(Uri u) async {
    final key = '${u.scheme}://${u.host}';
    if (!robots.containsKey(key)) {
      try {
        final (code, body) = await http('GET', Uri.parse('$key/robots.txt'), const {}, null);
        robots[key] = code == 200 ? Robots.parse(body) : Robots(const [], const [], const []);
      } catch (_) {
        robots[key] = Robots(const ['/'], const [], const []); // unreachable: stay away
      }
    }
    return robots[key]!.allows(u.path.isEmpty ? '/' : u.path);
  }

  // Candidate pages: named on the record, then the maker's sitemap.
  final pages = <Uri>[
    for (final d in p.docs)
      if (Uri.tryParse('${d['url'] ?? ''}') case final u? when u.hasScheme) u,
  ];
  final site = sites[p.maker!.toLowerCase().trim()];
  final reasons = <String>[];
  if (site != null) {
    final home = Uri.parse('https://www.$site');
    if (await allowed(home)) {
      final maps = [...robots['${home.scheme}://${home.host}']!.sitemaps];
      if (maps.isEmpty) maps.add('https://www.$site/sitemap.xml');
      final slug = _norm(model);
      var read = 0;
      final queue = [...maps];
      while (queue.isNotEmpty && read < 12 && pages.length < 8) {
        final m = Uri.tryParse(queue.removeAt(0));
        if (m == null || m.path.endsWith('.gz') || !await allowed(m)) continue;
        read++;
        try {
          final (code, xml) = await http('GET', m, const {}, null);
          if (code != 200) continue;
          final locs = [for (final x in RegExp(r'<loc>\s*([^<\s]+)\s*</loc>').allMatches(xml)) x.group(1)!.replaceAll('&amp;', '&')];
          if (xml.contains('<sitemapindex')) {
            // Product sitemaps first.
            locs.sort((a, b) => (b.contains('product') ? 1 : 0).compareTo(a.contains('product') ? 1 : 0));
            queue.addAll(locs.take(20));
            continue;
          }
          for (final l in locs) {
            final u = Uri.tryParse(l);
            if (u == null) continue;
            // The model as a whole path segment or a dash-separated part.
            final segs = u.pathSegments.map((s) => s.toLowerCase());
            if (segs.any((s) => _norm(s) == slug || s.split(RegExp(r'[-_.]')).any((t) => t == slug || t == model.toLowerCase())) || (slug.length >= 6 && _norm(u.path).contains(slug))) {
              pages.add(u);
            }
          }
        } catch (e) {
          reasons.add('sitemap ${m.host}: $e');
        }
      }
    } else {
      reasons.add('$site does not allow fetching');
    }
  } else {
    reasons.add('no known site for ${p.maker} (add it to model/photos.json)');
  }

  // Best page: names the model (and revision); prefer shorter addresses.
  final rack = _rackUnits(p);
  PhotoResult? review;
  for (final page in pages.toSet().take(8)) {
    if (!await allowed(page)) continue;
    String html;
    try {
      final (code, body) = await http('GET', page, const {}, null);
      if (code != 200) continue;
      html = body;
    } catch (_) {
      continue;
    }
    final text = html.replaceAll(RegExp(r'<script[\s\S]*?</script>|<style[\s\S]*?</style>', caseSensitive: false), ' ').replaceAll(RegExp(r'<[^>]+>'), ' ');
    final exact = namesModel(text, model);
    final revOk = revision == null || namesModel(text, revision);
    for (final src in [...pageImages(page, html, model), for (final i in p.images) if (Uri.tryParse(i) case final u? when u.hasScheme) u].take(4)) {
      if (!await allowed(src)) continue;
      img.Image? im;
      try {
        final (code, data, _) = await bytes(src, limit: 8 * 1024 * 1024);
        if (code != 200) continue;
        im = img.decodeImage(data is Uint8List ? data : Uint8List.fromList(data));
      } catch (_) {
        continue;
      }
      if (im == null) continue;
      final c = cropToObject(im);
      if (c == null) continue;
      var (cropped, box) = c;
      if (cropped.width > 1600) cropped = img.copyResize(cropped, width: 1600);
      final ratio = box[2] / box[3];
      final why = <String>[];
      if (!exact) why.add('the page does not name $model as a whole word');
      if (!revOk) why.add('the page does not say revision $revision');
      if (rack != null) {
        final want = 19 / (1.75 * rack);
        if ((ratio - want).abs() / want > 0.25) why.add('crop is ${ratio.toStringAsFixed(1)}:1, a ${rack}U front is about ${want.toStringAsFixed(1)}:1');
      } else if (ratio < 1) {
        why.add('taller than wide: probably not a front view');
      }
      final r = PhotoResult(why.isEmpty ? 'confirmed' : 'review', why.isEmpty ? ['$model${revision == null ? '' : ' ($revision)'} named on the page${rack == null ? '' : '; fits ${rack}U'}'] : why,
          page: page.toString(), image: src.toString(), crop: box, size: [im.width, im.height], ratio: double.parse(ratio.toStringAsFixed(2)), jpg: img.encodeJpg(cropped, quality: 85));
      if (r.status == 'confirmed') return r;
      review ??= r;
    }
  }
  return review ?? PhotoResult('none', [...reasons, if (pages.isEmpty) 'no page for $model found', if (pages.isNotEmpty) 'no usable front image on ${pages.length} page${pages.length == 1 ? '' : 's'}']);
}

/// Finds front photos for active products that have none yet (or whose
/// model changed). Runs on the fetching computer. Returns log lines.
Future<List<String>> updateFrontPhotos(String root, String me, ProductStore products, DateTime now,
    {Http http = realHttp, HttpBytes bytes = realHttpBytes, Duration retry = const Duration(days: 7)}) async {
  final log = <String>[];
  final sites = {...makerSites};
  try {
    final j = jsonDecode(File('$root/model/photos.json').readAsStringSync());
    if (j is Map && j['makers'] is Map) {
      for (final e in (j['makers'] as Map).entries) {
        sites['${e.key}'.toLowerCase()] = '${e.value}'.replaceAll(RegExp(r'^https?://(www\.)?'), '').replaceAll(RegExp(r'/.*'), '');
      }
    }
  } catch (_) {}
  for (final id in products.ids()) {
    final p = products.get(id);
    if (p == null || p.state != 'active' || p.maker == null || p.model == null) continue;
    final (model, revision) = modelAndRevision(p);
    final meta = File('$root/model/products/$id/front.json');
    try {
      final old = jsonDecode(meta.readAsStringSync()) as Map;
      final same = old['maker'] == p.maker && old['model'] == model && old['revision'] == revision;
      final t = DateTime.fromMillisecondsSinceEpoch(((old['t'] as num?) ?? 0).toInt() * 1000, isUtc: true);
      // Kept until the model changes; a miss is retried weekly.
      if (same && (old['status'] != 'none' || now.difference(t) < retry)) continue;
    } catch (_) {}
    final r = await findFrontPhoto(p, http: http, bytes: bytes, sites: sites);
    meta.parent.createSync(recursive: true);
    if (r.jpg != null) {
      final tmp = File('$root/model/products/$id/front.jpg.tmp')..writeAsBytesSync(r.jpg!, flush: true);
      tmp.renameSync('$root/model/products/$id/front.jpg');
    }
    final out = {
      'status': r.status, 'maker': p.maker, 'model': model, if (revision != null) 'revision': revision,
      if (r.page != null) 'page': r.page, if (r.image != null) 'image': r.image, if (r.crop != null) 'crop': r.crop,
      if (r.size != null) 'size': r.size, if (r.ratio != null) 'ratio': r.ratio,
      'reasons': r.reasons, 't': now.millisecondsSinceEpoch ~/ 1000, 'by': me,
    };
    final tmp = File('${meta.path}.tmp')..writeAsStringSync(jsonEncode(out), flush: true);
    tmp.renameSync(meta.path);
    log.add('photo: ${p.name}: ${r.status}${r.reasons.isEmpty ? '' : ' (${r.reasons.first})'}');
  }
  return log;
}
