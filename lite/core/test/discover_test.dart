import 'dart:convert';
import 'dart:io';

import 'package:monitor_core/monitor_core.dart';
import 'package:test/test.dart';

void main() {
  test('connectors are read from listing titles, in order', () {
    expect(connectorsInTitle('Anker USB C to USB A Cable 6ft'), ['usb-c', 'usb-a']);
    expect(connectorsInTitle('Cat6 Ethernet Patch Cable 3 ft RJ45'), ['rj45']);
    expect(connectorsInTitle('Mini HDMI to HDMI adapter'), ['hdmi-c', 'hdmi-a']);
    expect(connectorsInTitle('Phone case, blue'), isEmpty);
  });

  final srvUsb = Port('Server usb1', 'usb-a', dir: 'out', signals: ['usb-2', 'usb-3-5g']);
  final motu = Port('MOTU usb', 'usb-c', dir: 'in', signals: ['usb-2']);
  FetchedOffer o(String title, double price) => FetchedOffer('ebay', Offer(seller: 'eBay: s', price: price, currency: 'USD', url: 'https://www.ebay.com/itm/1'), title: title);

  test('a listing is judged against the two ports, either order in the title', () {
    final good = judge(o('USB-C to USB-A cable 2 m', 9), srvUsb, motu);
    expect(good.verdict, isNot('fail'));
    expect((good.a, good.b), ('usb-a', 'usb-c'));
    expect(good.evidence, isNot('verified'));
    expect(judge(o('USB-C to USB-C cable', 7), srvUsb, motu).verdict, 'fail');
    final blank = judge(o('Great cable!!', 3), srvUsb, motu);
    expect(blank.verdict, 'unknown');
    expect(blank.reasons.single, contains('names no connector'));
  });

  test('the rack scenario\'s missing parts get candidates from a fake shop', () async {
    final root = Directory.systemTemp.createTempSync('disc');
    addTearDown(() => root.deleteSync(recursive: true));
    final now = DateTime.utc(2026, 11, 28, 1);
    final items = ModelStore(root.path, 'beta');
    Item it(String id, String name, List<Map<String, Object?>> ports) => Item(id: id, type: 'device', name: name, state: 'owned', ports: ports);
    for (final i in [
      it('srv', 'wc-desktop', [{'id': 'eth0', 'kind': 'rj45', 'signals': ['eth-1g']}, {'id': 'usb1', 'kind': 'usb-a', 'dir': 'out', 'signals': ['usb-2']}]),
      it('sw', 'Switch', [{'id': 'p1', 'kind': 'rj45', 'signals': ['eth-1g']}]),
      it('motu', 'MOTU', [{'id': 'usb', 'kind': 'usb-c', 'dir': 'in', 'signals': ['usb-2']}]),
    ]) {
      items.put(i, now: now);
    }
    ProjectStore(root.path, 'beta').put(Project.fromJson({
      'id': 'rack', 'name': 'Rack',
      'assemblies': [{'id': 'r', 'name': 'Rack', 'type': 'rack', 'units': 12, 'placements': [
        {'id': 'p-srv', 'name': 'Server', 'item': 'srv', 'u': [3, 6]},
        {'id': 'p-sw', 'name': 'Switch', 'item': 'sw', 'u': [7, 7]},
        {'id': 'p-motu', 'name': 'MOTU', 'item': 'motu', 'u': [8, 8]},
      ]}],
      'connections': [
        {'id': 'net', 'from': {'placement': 'p-sw', 'port': 'p1'}, 'to': {'placement': 'p-srv', 'port': 'eth0'}},
        {'id': 'audio', 'from': {'placement': 'p-srv', 'port': 'usb1'}, 'to': {'placement': 'p-motu', 'port': 'usb'}, 'via': [{'name': 'USB-A cable', 'a': 'usb-a', 'b': 'usb-a'}]},
      ],
    })!, now: now);

    final queries = <String>[];
    Future<(int, String)> http(String m, Uri u, Map<String, String> h, String? b) async {
      if (u.path.endsWith('/token')) return (200, jsonEncode({'access_token': 't', 'expires_in': 7200}));
      final q = u.queryParameters['q']!;
      queries.add(q);
      final titles = q.contains('Ethernet')
          ? ['Cat6 Ethernet Patch Cable 3ft', 'Cat6 Ethernet Cable 10ft', 'RJ45 Cat5e Patch 1ft', 'Ethernet to USB-C adapter']
          : ['USB C to USB A Cable 3ft', 'USB-A to USB-C cord', 'USB-C to USB-C cable', 'Micro USB to USB-A cable'];
      var price = 5.0;
      return (200, jsonEncode({'itemSummaries': [for (final t in titles) {'title': t, 'price': {'value': '${price++}', 'currency': 'USD'}, 'seller': {'username': 's'}}]}));
    }

    final id = requestDiscover(root.path, 'pixel', 'rack', now: now);
    final log = await answerDiscoverRequests(root.path, 'beta', [EbaySource('id', 'secret')], now, http: http);
    expect(log.single, 'discover: rack: 2 missing parts searched');
    final a = discoverAnswer(root.path, id)!;
    final needs = (a['needs'] as List).cast<Map>();
    expect(queries, ['Cat6 Ethernet patch cable', 'USB-A to USB-C cable']);
    final net = needs.firstWhere((n) => n['name'] == '8P8C (RJ45) cable');
    expect((net['candidates'] as List).map((c) => c['title']), ['Cat6 Ethernet Patch Cable 3ft', 'Cat6 Ethernet Cable 10ft', 'RJ45 Cat5e Patch 1ft']);
    final usb = needs.firstWhere((n) => n['name'] == 'adapter');
    final titles = (usb['candidates'] as List).map((c) => c['title']).toList();
    expect(titles, ['USB C to USB A Cable 3ft', 'USB-A to USB-C cord']);
    // Answered once.
    expect(await answerDiscoverRequests(root.path, 'beta', [EbaySource('id', 'secret')], now, http: http), isEmpty);
  });

  test('no shop key: the answer says why', () async {
    final root = Directory.systemTemp.createTempSync('disc');
    addTearDown(() => root.deleteSync(recursive: true));
    final now = DateTime.utc(2026, 11, 28, 1);
    ProjectStore(root.path, 'beta').put(Project(id: 'p', name: 'P'), now: now);
    final id = requestDiscover(root.path, 'pixel', 'p', now: now);
    await answerDiscoverRequests(root.path, 'beta', sourcesFrom(root.path, env: const {}), now);
    expect(discoverAnswer(root.path, id)!['error'], contains('developer app key'));
  });
}
