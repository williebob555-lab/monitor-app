import 'dart:convert';
import 'dart:io';
import 'dart:typed_data';

import 'package:image/image.dart' as img;
import 'package:monitor_core/monitor_core.dart';
import 'package:test/test.dart';

/// A white picture with a dark 1U-shaped box (19 x 1.75) in it.
List<int> frontPng({int w = 640, int h = 240, double ratio = 19 / 1.75}) {
  final im = img.Image(width: w, height: h)..clear(img.ColorRgb8(255, 255, 255));
  final bw = 540, bh = (540 / ratio).round();
  img.fillRect(im, x1: 50, y1: 80, x2: 50 + bw - 1, y2: 80 + bh - 1, color: img.ColorRgb8(30, 30, 30));
  return img.encodePng(im);
}

void main() {
  test('whole-word model match; revision split off', () {
    expect(namesModel('MOTU 828 (2024) audio interface', '828'), isTrue);
    expect(namesModel('The 828es is great', '828'), isFalse);
    expect(namesModel('CyberPower CP1500 PFCRM2U', 'CP1500PFCRM2U'), isFalse); // no separator in the model to bridge
    expect(namesModel('CP1500PFCRM2U rack UPS', 'CP1500PFCRM2U'), isTrue);
    expect(modelAndRevision(Product(id: 'm', name: 'M', maker: 'MOTU', model: '828 (2024)')), ('828', '2024'));
  });

  test('robots.txt: longest rule wins, sitemaps listed', () {
    final r = Robots.parse('User-agent: *\nDisallow: /private\nAllow: /private/ok\nSitemap: https://www.motu.com/sitemap.xml\n\nUser-agent: Bad\nDisallow: /');
    expect(r.allows('/products/828/'), isTrue);
    expect(r.allows('/private/x'), isFalse);
    expect(r.allows('/private/ok/1'), isTrue);
    expect(r.sitemaps, ['https://www.motu.com/sitemap.xml']);
  });

  test('crop finds the chassis on a plain background', () {
    final im = img.decodePng(Uint8List.fromList(frontPng()))!;
    final (c, box) = cropToObject(im)!;
    expect(box[0], inInclusiveRange(40, 50));
    expect(c.width / c.height, closeTo(19 / 1.75, 1.2));
  });

  group('from the maker\'s site', () {
    late Directory root;
    setUp(() => root = Directory.systemTemp.createTempSync('photo'));
    tearDown(() => root.deleteSync(recursive: true));
    final now = DateTime.utc(2026, 11, 28, 2);
    var calls = <String>[];
    var pages = <String, String>{};

    Future<(int, String)> http(String m, Uri u, Map<String, String> h, String? b) async {
      calls.add(u.toString());
      if (u.path == '/robots.txt') return (200, 'User-agent: *\nDisallow: /private\nSitemap: https://www.motu.com/sitemap_index.xml');
      if (u.path == '/sitemap_index.xml') return (200, '<sitemapindex><sitemap><loc>https://www.motu.com/blog.xml</loc></sitemap><sitemap><loc>https://www.motu.com/product-sitemap.xml</loc></sitemap></sitemapindex>');
      if (u.path == '/product-sitemap.xml') {
        return (200, '<urlset>${[for (final p in pages.keys) '<url><loc>https://www.motu.com$p</loc></url>'].join()}<url><loc>https://www.motu.com/private/828/</loc></url></urlset>');
      }
      if (u.path == '/blog.xml') return (200, '<urlset></urlset>');
      final page = pages[u.path];
      return page == null ? (404, '') : (200, page);
    }

    Future<(int, List<int>, String?)> bytes(Uri u, {int limit = 0}) async {
      calls.add(u.toString());
      return (200, frontPng(), 'image/png');
    }

    test('exact model + revision + 1U shape: confirmed, cropped, cached', () async {
      calls = [];
      pages = {
        '/en-us/products/pro-audio/828/': '<html><head><meta property="og:image" content="/img/828-front.png"></head><body><h1>MOTU 828 (2024)</h1></body></html>',
        '/en-us/products/pro-audio/828es/': '<html><body>828es</body></html>',
      };
      final store = ProductStore(root.path, 'beta')..put(Product(id: 'motu-828', name: 'MOTU 828', maker: 'MOTU', model: '828 (2024)', extra: {'rackUnits': 1}), now: now);
      final log = await updateFrontPhotos(root.path, 'beta', store, now, http: http, bytes: bytes);
      expect(log.single, startsWith('photo: MOTU 828: confirmed'));
      final j = jsonDecode(File('${root.path}/model/products/motu-828/front.json').readAsStringSync()) as Map;
      expect(j['page'], 'https://www.motu.com/en-us/products/pro-audio/828/');
      expect(j['image'], 'https://www.motu.com/img/828-front.png');
      expect(j['revision'], '2024');
      expect((j['crop'] as List).length, 4);
      expect(File('${root.path}/model/products/motu-828/front.jpg').existsSync(), isTrue);
      expect(calls.any((c) => c.contains('/private/')), isFalse); // robots.txt obeyed
      calls = [];
      expect(await updateFrontPhotos(root.path, 'beta', store, now.add(const Duration(days: 3)), http: http, bytes: bytes), isEmpty);
      expect(calls, isEmpty);
    });

    test('only a similar model on the site: left for review', () async {
      pages = {'/en-us/products/pro-audio/828x/': '<html><head><meta property="og:image" content="/img/x.png"></head><body>MOTU 828x</body></html>'};
      // 828x is a different path part; give it via the record's docs instead.
      final p = Product(id: 'motu-828', name: 'MOTU 828', maker: 'MOTU', model: '828 (2024)', extra: {'rackUnits': 1},
          docs: [{'title': 'page', 'url': 'https://www.motu.com/en-us/products/pro-audio/828x/'}]);
      final r = await findFrontPhoto(p, http: http, bytes: bytes);
      expect(r.status, 'review');
      expect(r.reasons, contains('the page does not name 828 as a whole word'));
    });

    test('wrong shape for the rack units: review', () async {
      pages = {'/products/cp1500pfcrm2u/': '<html><head><meta property="og:image" content="/i.png"></head><body>CP1500PFCRM2U</body></html>'};
      final p = Product(id: 'ups', name: 'UPS', maker: 'MOTU', model: 'CP1500PFCRM2U', extra: {'rackUnits': 2});
      final r = await findFrontPhoto(p, http: http, bytes: bytes);
      expect(r.status, 'review');
      expect(r.reasons.single, contains('2U front is about 5.4:1'));
    });
  });
}

