import 'dart:convert';
import 'dart:io';

import 'package:monitor_core/monitor_core.dart';
import 'package:test/test.dart';

void main() {
  late Directory root;
  setUp(() => root = Directory.systemTemp.createTempSync('price'));
  tearDown(() => root.deleteSync(recursive: true));
  final t0 = DateTime.utc(2026, 11, 20, 12);
  final p = Product(id: 'ups', name: 'UPS', extra: {'watch': {'below': 300}});

  PricePoint pt(DateTime t, List<Offer> os) => PricePoint.of(t, os);
  Offer o(double price, {bool? inStock, String seller = 'eBay: a'}) => Offer(seller: seller, price: price, currency: 'USD', inStock: inStock);

  test('cheapest in-stock offer is the price point', () {
    final x = pt(t0, [o(320), o(290, inStock: false), o(310, seller: 'eBay: b')]);
    expect(x.best, 310);
    expect(x.seller, 'eBay: b');
    expect(x.inStock, 2);
    expect(x.offers, 3);
  });

  test('price.below fires on the crossing only', () {
    final above = pt(t0, [o(320)]);
    final under = pt(t0.add(const Duration(days: 1)), [o(295)]);
    final still = pt(t0.add(const Duration(days: 2)), [o(290)]);
    final ev = priceEvents(p, above, under, under.t);
    expect(ev.map((e) => e.type), ['price.below', 'price.changed']);
    expect(ev.first.subject, 'product:ups');
    expect(ev.first.detail, contains('295.00 USD'));
    expect(priceEvents(p, under, still, still.t).map((e) => e.type), ['price.changed']);
    // First ever fetch already under: that is a crossing too.
    expect(priceEvents(p, null, under, under.t).map((e) => e.type), ['price.below']);
  });

  test('price.instock after a fetch where everything was out of stock', () {
    final out = pt(t0, [o(320, inStock: false)]);
    final back = pt(t0.add(const Duration(days: 1)), [o(330)]);
    expect(priceEvents(p, out, back, back.t).map((e) => e.type), ['price.instock']);
  });

  test('two fetches: history in order, events in the fetching device', () async {
    final store = ProductStore(root.path, 'alpha')..put(p, now: t0);
    var price = '320.00';
    Future<(int, String)> http(String m, Uri u, Map<String, String> h, String? b) async => u.path.endsWith('/token')
        ? (200, jsonEncode({'access_token': 't', 'expires_in': 7200}))
        : (200, jsonEncode({'itemSummaries': [{'title': 'UPS', 'price': {'value': price, 'currency': 'USD'}, 'seller': {'username': 's'}}]}));
    final src = [EbaySource('id', 'secret')];
    await fetchOffers(root.path, store, src, t0, http: http, gap: Duration.zero, me: 'alpha');
    price = '289.99';
    final t1 = t0.add(const Duration(days: 1));
    final log = await fetchOffers(root.path, store, src, t1, http: http, gap: Duration.zero, me: 'alpha');
    expect(log, contains(startsWith('event price.below UPS')));
    final h = priceHistory(root.path, 'ups');
    expect(h.map((x) => x.best), [320.0, 289.99]);
    final ev = readEvents(root.path, t0.subtract(const Duration(days: 1)), t1.add(const Duration(hours: 1)));
    expect(ev.map((e) => e.type).toSet(), {'price.below', 'price.changed'});
    expect(ev.every((e) => e.device == 'alpha'), isTrue);
  });

  test('compaction: old lines shrink to one per day per source, a year back dropped', () {
    final f = File('${root.path}/offers.jsonl');
    final now = DateTime.utc(2026, 11, 28);
    final lines = <String>[];
    String line(DateTime t, double price) => jsonEncode({'t': t.millisecondsSinceEpoch ~/ 1000, 'source': 'ebay', 'seller': 'eBay: x', 'price': price, 'title': 'x' * 400});
    for (var i = 0; i < 400; i++) {
      lines.add(line(now.subtract(Duration(days: 60, minutes: i)), 300.0 + i)); // one day, 60 days back
    }
    lines.add(line(now.subtract(const Duration(days: 400)), 1)); // over a year
    for (var i = 0; i < 300; i++) {
      lines.add(line(now.subtract(Duration(days: 2, minutes: i)), 400)); // recent: all kept
    }
    f.writeAsStringSync(lines.map((l) => '$l\n').join());
    expect(f.lengthSync(), greaterThan(256 * 1024));
    compactOffers(f, now);
    final kept = f.readAsLinesSync().map((l) => jsonDecode(l) as Map).toList();
    final old = kept.where((j) => (j['t'] as int) < now.subtract(const Duration(days: 30)).millisecondsSinceEpoch ~/ 1000).toList();
    expect(old.map((j) => j['price']), everyElement(lessThan(302))); // cheapest per day
    expect(old.length, lessThanOrEqualTo(2)); // 400 minutes may straddle midnight
    expect(kept.where((j) => j['price'] == 1), isEmpty);
    expect(kept.where((j) => j['price'] == 400), hasLength(300));
  });
}
