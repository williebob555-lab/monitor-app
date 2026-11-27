import 'dart:io';

import 'package:monitor_core/monitor_core.dart';
import 'package:test/test.dart';

void main() {
  test('a product added on one device is linked to an item on another', () {
    final dir = Directory.systemTemp.createTempSync('products');
    addTearDown(() => dir.deleteSync(recursive: true));
    final t = DateTime.utc(2026, 11, 27, 18);
    // The Pixel adds the product.
    ProductStore(dir.path, 'pixel').put(Product(id: 'cps-or500', name: 'CyberPower OR500LCDRM1U', maker: 'CyberPower', model: 'OR500LCDRM1U',
        specs: {'capacityVA': Prop(500, unit: 'VA'), 'outputW': Prop(300, unit: 'W')},
        docs: [{'title': 'User manual', 'url': 'https://example.com/or500.pdf'}],
        offers: [Offer(seller: 'A', price: 199.99, currency: 'USD', inStock: false), Offer(seller: 'B', price: 214.5, currency: 'USD', inStock: true)]), now: t);
    // The laptop owns the UPS item and links it.
    final items = ModelStore(dir.path, 'laptop');
    final ups = items.put(Item(id: 'ups', type: 'ups', name: 'Desk UPS', state: 'owned'), now: t);
    ProductStore(dir.path, 'laptop').link(items, ups, 'cps-or500', now: t.add(const Duration(minutes: 1)));
    // Any device reads it back.
    final p = ProductStore(dir.path, 'tablet').of(ModelStore(dir.path, 'tablet').get('ups')!)!;
    expect(p.specs['outputW']!.value, 300);
    expect(p.by, 'pixel');
    expect(p.bestOffer()!.seller, 'B');
    expect(() => ProductStore(dir.path, 'x').link(items, ups, '../x'), throwsArgumentError);
  });
}
