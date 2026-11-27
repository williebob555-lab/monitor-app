import 'model.dart';

/// Product records (roadmap row 30): `model/products/<id>/product.json`,
/// versioned like items (history per device, conflict copies resolved by
/// newest). A product is what can be bought (maker, model, specs, images,
/// manuals, offers); an item is the one you own or plan. An item points at
/// its product with `product` (kept in the item's extra fields), and a BOM
/// line through its item. Manual entry first; a fetcher (row 31) may add
/// offers later, each with where and when it was seen.
const productStates = ['active', 'retired'];

class Offer {
  Offer({required this.seller, this.price, this.currency, this.url, this.inStock, this.seen});
  final String seller;
  final double? price;
  final String? currency, url;
  final bool? inStock;
  final DateTime? seen;

  static Offer? fromJson(Object? j) {
    if (j is! Map || j['seller'] is! String) return null;
    final t = j['seen'];
    return Offer(
        seller: j['seller'] as String,
        price: (j['price'] as num?)?.toDouble(),
        currency: j['currency'] as String?,
        url: j['url'] as String?,
        inStock: j['inStock'] as bool?,
        seen: t is num ? DateTime.fromMillisecondsSinceEpoch((t * 1000).round(), isUtc: true) : null);
  }

  Map<String, Object?> toJson() => {
        'seller': seller,
        if (price != null) 'price': price,
        if (currency != null) 'currency': currency,
        if (url != null) 'url': url,
        if (inStock != null) 'inStock': inStock,
        if (seen != null) 'seen': seen!.millisecondsSinceEpoch ~/ 1000,
      };
}

class Product implements Versioned {
  Product({required this.id, required this.name, this.maker, this.model, this.state = 'active', Map<String, Prop>? specs, List<String>? images, List<Map<String, Object?>>? docs, List<Offer>? offers, this.rev = 0, this.by, this.time, Map<String, Object?>? extra})
      : specs = specs ?? {},
        images = images ?? [],
        docs = docs ?? [],
        offers = offers ?? [],
        extra = extra ?? {};

  @override
  final String id;
  String name;
  String? maker, model;
  @override
  String state;

  /// Specs with where each came from (`declared` for a datasheet).
  final Map<String, Prop> specs;

  /// Image URLs (front photo first).
  final List<String> images;

  /// Manuals and datasheets: `{"title", "url"}`.
  final List<Map<String, Object?>> docs;
  final List<Offer> offers;
  @override
  int rev;
  @override
  String? by;
  @override
  DateTime? time;
  final Map<String, Object?> extra;

  static const _known = {'v', 'id', 'name', 'maker', 'model', 'state', 'specs', 'images', 'docs', 'offers', 'rev', 'by', 't'};

  static Product? fromJson(Object? j) {
    if (j is! Map || j['id'] is! String || !validModelId(j['id'] as String) || j['name'] is! String) return null;
    final t = j['t'];
    return Product(
      id: j['id'] as String,
      name: j['name'] as String,
      maker: j['maker'] as String?,
      model: j['model'] as String?,
      state: productStates.contains(j['state']) ? j['state'] as String : 'active',
      specs: {
        if (j['specs'] is Map)
          for (final e in (j['specs'] as Map).entries)
            if (e.key is String && Prop.fromJson(e.value) != null) e.key as String: Prop.fromJson(e.value)!
      },
      images: [if (j['images'] is List) for (final x in j['images'] as List) if (x is String) x],
      docs: [if (j['docs'] is List) for (final x in j['docs'] as List) if (x is Map) x.cast<String, Object?>()],
      offers: [if (j['offers'] is List) for (final x in j['offers'] as List) if (Offer.fromJson(x) case final o?) o],
      rev: (j['rev'] as num?)?.toInt() ?? 0,
      by: j['by'] as String?,
      time: t is num ? DateTime.fromMillisecondsSinceEpoch((t * 1000).round(), isUtc: true) : null,
      extra: {for (final e in j.entries) if (e.key is String && !_known.contains(e.key)) e.key as String: e.value},
    );
  }

  @override
  Map<String, Object?> toJson() => {
        ...extra,
        'v': 1,
        'id': id,
        'name': name,
        if (maker != null) 'maker': maker,
        if (model != null) 'model': model,
        'state': state,
        'specs': {for (final e in specs.entries) e.key: e.value.toJson()},
        'images': images,
        'docs': docs,
        'offers': [for (final o in offers) o.toJson()],
        'rev': rev,
        if (by != null) 'by': by,
        if (time != null) 't': time!.millisecondsSinceEpoch / 1000,
      };

  /// The cheapest offer in stock (or of unknown stock), in [currency].
  Offer? bestOffer({String? currency}) {
    final ok = [for (final o in offers) if (o.price != null && o.inStock != false && (currency == null || o.currency == currency)) o]..sort((a, b) => a.price!.compareTo(b.price!));
    return ok.firstOrNull;
  }
}

class ProductStore extends VersionedStore<Product> {
  ProductStore(String root, String deviceId)
      : super(root, deviceId, collection: 'products', fileBase: 'product', parse: Product.fromJson, validate: (p) {
          if (!productStates.contains(p.state)) throw ArgumentError('invalid state ${p.state}');
        });

  /// The product an item points at, if any.
  Product? of(Item it) => it.extra['product'] is String ? get(it.extra['product'] as String) : null;

  /// Points [it] at [productId] and writes it through [items].
  Item link(ModelStore items, Item it, String productId, {DateTime? now}) {
    if (!validModelId(productId)) throw ArgumentError('bad product id');
    it.extra['product'] = productId;
    return items.put(it, now: now);
  }
}
