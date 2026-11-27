import 'dart:convert';
import 'dart:io';

import 'package:monitor_core/monitor_core.dart';
import 'package:test/test.dart';

void main() {
  late Directory root;
  setUp(() => root = Directory.systemTemp.createTempSync('fetch'));
  tearDown(() => root.deleteSync(recursive: true));
  final now = DateTime.utc(2026, 11, 27, 22);

  Future<(int, String)> fake(String m, Uri u, Map<String, String> h, String? b) async {
    if (u.path.endsWith('/token')) {
      expect(h['Authorization'], startsWith('Basic '));
      return (200, jsonEncode({'access_token': 'tok', 'expires_in': 7200}));
    }
    expect(h['Authorization'], 'Bearer tok');
    expect(u.queryParameters['q'], 'CyberPower CP1500PFCRM2U');
    return (200, jsonEncode({
      'itemSummaries': [
        {'title': 'CyberPower CP1500PFCRM2U', 'price': {'value': '389.95', 'currency': 'USD'}, 'itemWebUrl': 'https://www.ebay.com/itm/1', 'seller': {'username': 'shop'}},
      ]
    }));
  }

  test('eBay offers land in offers.jsonl; keyless shops say why', () async {
    final store = ProductStore(root.path, 'alpha');
    store.put(Product(id: 'cp1500', name: 'UPS', maker: 'CyberPower', model: 'CP1500PFCRM2U'), now: now);
    final sources = [EbaySource('id', 'secret'), ...sourcesFrom(root.path, env: const {}).where((s) => s.id != 'ebay')];
    final log = await fetchOffers(root.path, store, sources, now, http: fake, gap: Duration.zero);
    expect(log.where((l) => l.contains('skipped')), hasLength(2));
    final lines = File('${root.path}/model/products/cp1500/offers.jsonl').readAsLinesSync();
    final o = jsonDecode(lines.single) as Map;
    expect(o['price'], 389.95);
    expect(o['source'], 'ebay');
    expect(o['seller'], 'eBay: shop');
  });

  test('no keys: eBay is skipped, nothing fetched', () async {
    final s = sourcesFrom(root.path, env: const {});
    expect(s.firstWhere((x) => x.id == 'ebay').missing, contains('developer app key'));
  });

  test('only the computer in model/fetch.json fetches', () {
    expect(fetcherDevice(root.path), isNull);
    Directory('${root.path}/model').createSync();
    File('${root.path}/model/fetch.json').writeAsStringSync('{"on":"ebad35097c54"}');
    expect(fetcherDevice(root.path), 'ebad35097c54');
  });
}
