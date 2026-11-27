import 'dart:io';
import 'dart:typed_data';

import 'package:monitor_core/monitor_core.dart';
import 'package:monitor_core/src/agent/netcheck.dart';
import 'package:test/test.dart';

final now = DateTime.utc(2026, 11, 27, 12);

Report dev(String id, {String internet = 'reachable', String dns = 'answers', String direct = 'answers', String gateway = 'answers', String? pihole}) => Report(
      id: id,
      name: id,
      kind: 'linux',
      time: now.subtract(const Duration(seconds: 20)),
      machine: 'm-$id',
      tailscaleIp: '100.64.0.${id.length}',
      syncthingLocal: true,
      nextSeconds: 60,
      readings: [
        Reading(key: 'net.internet', value: internet),
        Reading(key: 'net.dns', value: dns),
        Reading(key: 'net.dnsDirect', value: direct),
        Reading(key: 'net.gateway', value: gateway),
        if (pihole != null) Reading(key: 'net.pihole', value: pihole),
      ],
    );

NetworkStatus net(List<Report> l, {String? me}) => assess({for (final r in l) r.id: r}, myId: me, now: now).network;

void main() {
  test('internet down for everyone, router fine', () {
    final n = net([dev('beta', internet: 'no'), dev('pixel', internet: 'no'), dev('laptop', internet: 'no', gateway: 'no answer')]);
    expect(n.title, 'Internet down for everyone');
    expect(n.level, Level.fault);
    expect(n.detail, contains('line to the internet is down'));
  });

  test('stopped Pi-hole: DNS failing (Pi-hole) on every device', () {
    final n = net([dev('beta', pihole: 'stopped', dns: 'no answer'), dev('pixel', dns: 'no answer'), dev('laptop', dns: 'no answer')]);
    expect(n.title, 'DNS failing (Pi-hole)');
    expect(n.level, Level.fault);
    expect(n.detail, contains('Pi-hole on beta is stopped'));
    expect(n.detail, contains('Public DNS still answers'));
  });

  test('Pi-hole down but names still resolve: a warning only', () {
    final n = net([dev('beta', pihole: 'no answer'), dev('pixel')]);
    expect(n.title, 'Pi-hole not answering');
    expect(n.level, Level.warn);
  });

  test("one device's own connection", () {
    final n = net([dev('beta'), dev('pixel', internet: 'no', gateway: 'no answer')], me: 'pixel');
    expect(n.title, 'No internet on pixel');
    expect(n.detail, contains('Wi-Fi or cable'));
    expect(n.detail, contains('includes this device'));
  });

  test('all fine: no network layer status', () {
    expect(net([dev('beta'), dev('pixel')]).level, Level.ok);
  });

  test('parse route table and resolv.conf', () {
    const route = 'Iface\tDestination\tGateway \tFlags\n'
        'eth0\t00000000\t0101A8C0\t0003\n'
        'eth0\t0001A8C0\t00000000\t0001\n';
    expect(NetCheck.parseRoute(route), '192.168.1.1');
    expect(NetCheck.parseResolvConf('# x\nnameserver 127.0.0.53\nnameserver ::1\noptions edns0\n'), ['127.0.0.53']);
  });

  test('dnsQuery: NXDOMAIN counts as answering, SERVFAIL and silence do not', () async {
    Future<int?> against(int rcode) async {
      final server = await RawDatagramSocket.bind(InternetAddress.loopbackIPv4, 0);
      server.listen((e) {
        final d = server.receive();
        if (d == null) return;
        final reply = Uint8List.fromList(d.data);
        reply[2] = 0x81;
        reply[3] = 0x80 | rcode;
        if (rcode >= 0) server.send(reply, d.address, d.port);
      });
      final r = await NetCheck.dnsQuery('127.0.0.1', port: server.port, timeout: const Duration(milliseconds: 400));
      server.close();
      return r;
    }

    expect(await against(3), isNotNull);
    expect(await against(0), isNotNull);
    expect(await against(2), isNull);
  });

  test('reach: a refused port proves the host is there', () async {
    final s = await ServerSocket.bind(InternetAddress.loopbackIPv4, 0);
    final port = s.port;
    await s.close();
    expect(await NetCheck.reach('127.0.0.1', [port]), isNotNull);
  });
}
