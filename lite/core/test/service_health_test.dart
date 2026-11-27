import 'dart:io';

import 'package:monitor_core/src/agent/containers.dart';
import 'package:test/test.dart';

void main() {
  test('httpCheck gives the status and time, and null when nothing answers', () async {
    final server = await HttpServer.bind('127.0.0.1', 0);
    final port = server.port;
    server.listen((r) {
      r.response.statusCode = r.uri.path == '/down' ? 503 : 200;
      r.response.close();
    });
    final ok = await Containers.httpCheck('http://127.0.0.1:$port/');
    final down = await Containers.httpCheck('http://127.0.0.1:$port/down');
    await server.close(force: true);
    final none = await Containers.httpCheck('http://127.0.0.1:$port/', timeout: const Duration(milliseconds: 300));
    expect(ok.$1, 200);
    expect(down.$1, 503);
    expect(none.$1, isNull);
  });

  test('httpRequest posts JSON and reads the body', () async {
    final server = await HttpServer.bind('127.0.0.1', 0);
    server.listen((r) async {
      final body = await r.map((c) => c).expand((c) => c).toList();
      r.response.write('{"got":${String.fromCharCodes(body)}}');
      await r.response.close();
    });
    final res = await Containers.httpRequest('http://127.0.0.1:${server.port}/api/auth', method: 'POST', body: '{"password":"x"}');
    await server.close(force: true);
    expect(res!.$1, 200);
    expect(res.$2, '{"got":{"password":"x"}}');
  });

  test('Pi-hole summary becomes readings', () {
    final now = DateTime.utc(2026, 11, 27, 12);
    final last = now.subtract(const Duration(days: 3)).millisecondsSinceEpoch ~/ 1000;
    final r = PiHole.parseSummary('{"queries":{"total":12345,"blocked":2345,"percent_blocked":18.996},'
        '"gravity":{"domains_being_blocked":150000,"last_update":$last}}', now);
    final v = {for (final x in r) x.key: x.value};
    expect(v['ph.queries'], 12345);
    expect(v['ph.blocked'], 2345);
    expect(v['ph.blockedPct'], 19.0);
    expect(v['ph.domains'], 150000);
    expect(v['ph.gravityAge'], 3.0);
    expect(PiHole.parseSummary(null, now).single.level, 'warn');
  });
}
