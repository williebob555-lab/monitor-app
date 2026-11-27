import 'dart:convert';
import 'dart:io';

import 'products.dart';

/// Offer fetcher (roadmap row 31). One computer, named in
/// `model/fetch.json` (`{"on": "<device id>", "every": "24h"}`), asks the
/// shops Wilson picked (2026-11-27: Amazon, eBay, Walmart; mainstream only)
/// for each active product's offers, through their official APIs only, and
/// appends what it found to `model/products/<id>/offers.jsonl`, one JSON
/// object per line: `{"t", "source", "seller", "price", "currency", "url",
/// "inStock", "title"}`.
///
/// Keys never go in the shared folder: they are read from this computer's
/// own `keys.json` (next to its local settings, mode 0600) or the
/// environment. A source without keys is skipped and says so in the log.

/// One HTTP call; injectable for tests.
typedef Http = Future<(int, String)> Function(String method, Uri url, Map<String, String> headers, String? body);

Future<(int, String)> realHttp(String method, Uri url, Map<String, String> headers, String? body) async {
  final c = HttpClient()..connectionTimeout = const Duration(seconds: 10);
  c.userAgent = 'MonitorApp-offers/1 (+https://github.com/williebob555-lab/monitor-app)';
  try {
    final req = await c.openUrl(method, url).timeout(const Duration(seconds: 15));
    headers.forEach(req.headers.set);
    if (body != null) req.write(body);
    final res = await req.close().timeout(const Duration(seconds: 20));
    final text = await res.transform(utf8.decoder).join().timeout(const Duration(seconds: 20));
    return (res.statusCode, text.length > 512 * 1024 ? text.substring(0, 512 * 1024) : text);
  } finally {
    c.close(force: true);
  }
}

class FetchedOffer {
  FetchedOffer(this.source, this.offer, {this.title});
  final String source;
  final Offer offer;
  final String? title;

  Map<String, Object?> toJson(DateTime t) => {
        't': t.millisecondsSinceEpoch ~/ 1000,
        'source': source,
        ...offer.toJson()..remove('seen'),
        if (title != null) 'title': title,
      };
}

abstract class OfferSource {
  String get id;

  /// Null when the source can run; else why not, in words.
  String? get missing;
  Future<List<FetchedOffer>> search(String query, Http http);
}

/// eBay Browse API (official). Needs an eBay developer app's client id and
/// secret (`ebay.clientId`, `ebay.secret`); new, fixed-price US listings.
class EbaySource implements OfferSource {
  EbaySource(this.clientId, this.secret, {this.marketplace = 'EBAY_US'});
  final String? clientId, secret;
  final String marketplace;
  String? _token;
  DateTime? _tokenUntil;

  @override
  String get id => 'ebay';

  @override
  String? get missing => clientId == null || secret == null ? 'eBay needs a developer app key (ebay.clientId and ebay.secret)' : null;

  Future<String?> _auth(Http http) async {
    if (_token != null && _tokenUntil != null && DateTime.now().isBefore(_tokenUntil!)) return _token;
    final (code, body) = await http('POST', Uri.parse('https://api.ebay.com/identity/v1/oauth2/token'), {
      'Authorization': 'Basic ${base64.encode(utf8.encode('$clientId:$secret'))}',
      'Content-Type': 'application/x-www-form-urlencoded',
    }, 'grant_type=client_credentials&scope=${Uri.encodeComponent('https://api.ebay.com/oauth/api_scope')}');
    if (code != 200) return null;
    final j = jsonDecode(body);
    _token = j['access_token'] as String?;
    _tokenUntil = DateTime.now().add(Duration(seconds: ((j['expires_in'] as num?) ?? 3600).toInt() - 60));
    return _token;
  }

  @override
  Future<List<FetchedOffer>> search(String query, Http http) async {
    final token = await _auth(http);
    if (token == null) throw StateError('eBay refused the key');
    final url = Uri.https('api.ebay.com', '/buy/browse/v1/item_summary/search', {
      'q': query,
      'limit': '5',
      'filter': 'buyingOptions:{FIXED_PRICE},conditions:{NEW}',
    });
    final (code, body) = await http('GET', url, {'Authorization': 'Bearer $token', 'X-EBAY-C-MARKETPLACE-ID': marketplace}, null);
    if (code != 200) throw StateError('eBay answered $code');
    final items = (jsonDecode(body)['itemSummaries'] as List?) ?? const [];
    return [
      for (final i in items.whereType<Map>())
        if (i['price'] is Map)
          FetchedOffer(
              'ebay',
              Offer(
                  seller: 'eBay: ${(i['seller'] as Map?)?['username'] ?? '?'}',
                  price: double.tryParse('${(i['price'] as Map)['value']}'),
                  currency: (i['price'] as Map)['currency'] as String?,
                  url: i['itemWebUrl'] as String?,
                  inStock: true),
              title: i['title'] as String?),
    ];
  }
}

/// Amazon Product Advertising API and Walmart's affiliate API both need keys
/// from programs Wilson would have to join; until then they say so.
class KeyedSource implements OfferSource {
  KeyedSource(this.id, this.need);
  @override
  final String id;
  final String need;
  @override
  String? get missing => need;
  @override
  Future<List<FetchedOffer>> search(String query, Http http) async => const [];
}

/// Keys from this computer only: `keys.json` in [localDir], then the
/// environment (`MONITOR_EBAY_CLIENT_ID`, `MONITOR_EBAY_SECRET`).
List<OfferSource> sourcesFrom(String localDir, {Map<String, String>? env}) {
  final e = env ?? Platform.environment;
  Map<String, Object?> keys = const {};
  try {
    keys = (jsonDecode(File('$localDir${Platform.pathSeparator}keys.json').readAsStringSync()) as Map).cast<String, Object?>();
  } catch (_) {}
  String? k(String key, String envName) => (keys[key] as String?) ?? e[envName];
  return [
    EbaySource(k('ebay.clientId', 'MONITOR_EBAY_CLIENT_ID'), k('ebay.secret', 'MONITOR_EBAY_SECRET')),
    KeyedSource('amazon', 'Amazon needs Product Advertising API keys (an Amazon Associates account)'),
    KeyedSource('walmart', 'Walmart needs affiliate API keys (Walmart Affiliate Program)'),
  ];
}

/// Who fetches, from `model/fetch.json`; null = nobody (off).
String? fetcherDevice(String root) {
  try {
    final j = jsonDecode(File('$root/model/fetch.json').readAsStringSync());
    return j is Map ? j['on'] as String? : null;
  } catch (_) {
    return null;
  }
}

/// The search words for a product: maker and model when known, else its name.
String queryFor(Product p) => [if (p.maker != null) p.maker!, if (p.model != null) p.model!].join(' ').trim().isNotEmpty
    ? [if (p.maker != null) p.maker!, if (p.model != null) p.model!].join(' ')
    : p.name;

/// One pass over every active product. Returns log lines. At most one
/// request per source per product, a second apart.
Future<List<String>> fetchOffers(String root, ProductStore products, List<OfferSource> sources, DateTime now, {Http http = realHttp, Duration gap = const Duration(seconds: 1)}) async {
  final log = <String>[];
  final usable = sources.where((s) => s.missing == null).toList();
  for (final s in sources.where((s) => s.missing != null)) {
    log.add('offers: ${s.id} skipped: ${s.missing}');
  }
  if (usable.isEmpty) return log;
  for (final id in products.ids()) {
    final p = products.get(id);
    if (p == null || p.state != 'active') continue;
    final found = <FetchedOffer>[];
    for (final s in usable) {
      try {
        found.addAll(await s.search(queryFor(p), http));
      } catch (e) {
        log.add('offers: ${s.id} for ${p.name}: $e');
      }
      if (gap > Duration.zero) await Future<void>.delayed(gap);
    }
    if (found.isEmpty) continue;
    final f = File('$root/model/products/$id/offers.jsonl');
    f.parent.createSync(recursive: true);
    if (f.existsSync() && f.lengthSync() > 2 * 1024 * 1024) continue; // bounded; pruning is row 32's
    f.writeAsStringSync('${found.map((o) => jsonEncode(o.toJson(now))).join('\n')}\n', mode: FileMode.append, flush: true);
    log.add('offers: ${p.name}: ${found.length} from ${found.map((o) => o.source).toSet().join(', ')}');
  }
  return log;
}
