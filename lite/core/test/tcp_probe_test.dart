import 'dart:io';

import 'package:monitor_core/agent.dart';
import 'package:monitor_core/monitor_core.dart';
import 'package:test/test.dart';

void main() {
  test('the TCP handshake time is measured for open and refused', () async {
    ServerSocket? server;
    try {
      server = await ServerSocket.bind(InternetAddress.loopbackIPv4, syncthingPort);
    } on SocketException {
      markTestSkipped('port $syncthingPort is busy here');
      return;
    }
    server.listen((s) => s.destroy());
    final open = await tcpProbeTimed('127.0.0.1');
    expect(open.$1, 'open');
    expect(open.$2, isNotNull);
    await server.close();
    final refused = await tcpProbeTimed('127.0.0.1');
    expect(refused.$1, 'refused');
    expect(refused.$2, isNotNull);
  });

  test('tcpMs is kept in the report and read back', () {
    final p = PeerView.tryParse(PeerView(tcp: 'open', tcpMs: 7, seenAge: 30).toJson())!;
    expect(p.tcpMs, 7);
    expect(p.pingMs, isNull);
    expect(p.reachable, isTrue);
  });
}
