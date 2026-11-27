import 'dart:io';

import 'package:monitor_core/src/agent/containers.dart';
import 'package:test/test.dart';

void main() {
  test('only a port that answers HTTP becomes a link (not DNS on 53 or Syncthing on 22000)', () async {
    final web = await HttpServer.bind(InternetAddress.loopbackIPv4, 0);
    web.listen((r) => r.response.close());
    final silent = await ServerSocket.bind(InternetAddress.loopbackIPv4, 0); // answers TCP, says nothing
    silent.listen((_) {});
    final other = await ServerSocket.bind(InternetAddress.loopbackIPv4, 0); // answers with something else
    other.listen((s) => s.add([0x00, 0x1c, 0x12, 0x34, 0x81]));
    addTearDown(() async {
      await web.close(force: true);
      await silent.close();
      await other.close();
    });
    expect(await Containers.speaksHttp('127.0.0.1', web.port), isTrue);
    expect(await Containers.speaksHttp('127.0.0.1', silent.port, timeout: const Duration(milliseconds: 300)), isFalse);
    expect(await Containers.speaksHttp('127.0.0.1', other.port), isFalse);
  });
}
