import 'dart:async';
import 'dart:io';
import 'dart:math';
import 'dart:typed_data';

import '../report.dart';
import 'windows.dart' as win;

/// Which layer of the network works, from this device, each cycle:
/// its gateway (router), DNS through its own resolver (Pi-hole when that is
/// what the network hands out), DNS straight to a public resolver, and the
/// internet (one TCP connect). A handful of packets, all in parallel, each
/// with a short timeout; no child processes.
///
/// Readings (group Network): `net.gateway`, `net.dns`, `net.dnsDirect`,
/// `net.internet` (values `answers` / `no answer`, `reachable` / `no`), each
/// with the time in ms in its note, and `net.ifaces` (interfaces and
/// addresses). `assess()` turns them into "Internet down for everyone",
/// "DNS failing (Pi-hole)" and "No internet on <device>".
class NetCheck {
  static const publicDns = ['1.1.1.1', '9.9.9.9'];
  static const internet = [('1.1.1.1', 443), ('9.9.9.9', 443)];
  static final _rand = Random();

  static Future<List<Reading>> collect() async {
    final gw = gateway();
    final resolvers = systemResolvers();
    final results = await Future.wait([
      gw == null ? Future.value(null) : reach(gw, const [53, 80, 443]),
      resolvers.isEmpty ? _lookup() : firstAnswer(resolvers),
      firstAnswer(publicDns),
      firstReach(internet),
    ]);
    final g = results[0] as (String, int)?;
    final dns = results[1] as (String, int, String)?;
    final direct = results[2] as (String, int, String)?;
    final net = results[3] as (String, int)?;
    String ms(int v) => '$v ms';
    return [
      if (gw != null)
        Reading(key: 'net.gateway', label: 'Router', value: g == null ? 'no answer' : 'answers', group: 'Network', src: 'measured',
            note: g == null ? gw : '$gw, ${ms(g.$2)}', level: g == null ? 'warn' : null),
      Reading(key: 'net.dns', label: 'DNS (this network\'s)', value: dns == null ? 'no answer' : 'answers', group: 'Network', src: 'measured',
          note: dns == null ? (resolvers.isEmpty ? 'system lookup' : resolvers.join(', ')) : '${dns.$3}, ${ms(dns.$2)}',
          level: dns == null ? 'warn' : null),
      Reading(key: 'net.dnsDirect', label: 'DNS (public)', value: direct == null ? 'no answer' : 'answers', group: 'Network', src: 'measured',
          note: direct == null ? publicDns.join(', ') : '${direct.$3}, ${ms(direct.$2)}'),
      Reading(key: 'net.internet', label: 'Internet', value: net == null ? 'no' : 'reachable', group: 'Network', src: 'measured',
          note: net == null ? 'no answer from ${internet.map((e) => e.$1).join(' or ')}' : ms(net.$2), level: net == null ? 'warn' : null),
      if (await interfaces() case final i? when i.isNotEmpty) Reading(key: 'net.ifaces', label: 'Interfaces', value: i, group: 'Network', src: 'measured'),
    ];
  }

  /// The default route's gateway (Linux: /proc/net/route).
  static String? gateway() {
    if (!Platform.isLinux) return null;
    try {
      return parseRoute(File('/proc/net/route').readAsStringSync());
    } catch (_) {
      return null;
    }
  }

  static String? parseRoute(String table) {
    for (final line in table.split('\n').skip(1)) {
      final f = line.trim().split(RegExp(r'\s+'));
      if (f.length < 3 || f[1] != '00000000') continue;
      final h = int.tryParse(f[2], radix: 16);
      if (h == null || h == 0) continue;
      return [h & 0xff, (h >> 8) & 0xff, (h >> 16) & 0xff, (h >> 24) & 0xff].join('.');
    }
    return null;
  }

  /// The resolvers this device asks (Linux: resolv.conf; Windows: the
  /// adapters' DNS servers). Empty: use the system lookup.
  static List<String> systemResolvers() {
    try {
      if (Platform.isLinux) return parseResolvConf(File('/etc/resolv.conf').readAsStringSync());
      if (Platform.isWindows) return win.windowsDnsServers();
    } catch (_) {}
    return const [];
  }

  static List<String> parseResolvConf(String s) => [
        for (final l in s.split('\n'))
          if (RegExp(r'^\s*nameserver\s+(\S+)').firstMatch(l) case final m?)
            if (InternetAddress.tryParse(m.group(1)!)?.type == InternetAddressType.IPv4) m.group(1)!,
      ].take(3).toList();

  /// Up when it answers TCP or actively refuses (a refusal proves it is
  /// there). (address, ms) or null.
  static Future<(String, int)?> reach(String host, List<int> ports, {Duration timeout = const Duration(seconds: 2)}) async {
    final sw = Stopwatch()..start();
    final c = Completer<(String, int)?>();
    var left = ports.length;
    for (final p in ports) {
      Socket.connect(host, p, timeout: timeout).then((s) {
        s.destroy();
        if (!c.isCompleted) c.complete((host, sw.elapsedMilliseconds));
      }, onError: (Object e) {
        final refused = e is SocketException && (e.osError?.errorCode == 111 || e.osError?.errorCode == 10061 || '${e.osError?.message}'.toLowerCase().contains('refused'));
        if (refused && !c.isCompleted) c.complete((host, sw.elapsedMilliseconds));
        if (--left == 0 && !c.isCompleted) c.complete(null);
      });
    }
    return c.future;
  }

  static Future<(String, int)?> firstReach(List<(String, int)> targets) async {
    for (final (h, p) in targets) {
      final sw = Stopwatch()..start();
      try {
        final s = await Socket.connect(h, p, timeout: const Duration(seconds: 2));
        s.destroy();
        return (h, sw.elapsedMilliseconds);
      } catch (_) {}
    }
    return null;
  }

  /// The first server of [servers] that answers a DNS query: (status, ms, server).
  static Future<(String, int, String)?> firstAnswer(List<String> servers, {int port = 53}) async {
    for (final s in servers) {
      final ms = await dnsQuery(s, port: port);
      if (ms != null) return ('answers', ms, s);
    }
    return null;
  }

  /// One A query for a random name under example.com, so no cache can answer
  /// it: NOERROR or NXDOMAIN means the resolver works end to end; SERVFAIL,
  /// REFUSED or silence means it doesn't. Returns ms or null.
  static Future<int?> dnsQuery(String server, {int port = 53, Duration timeout = const Duration(seconds: 2)}) async {
    final addr = InternetAddress.tryParse(server);
    if (addr == null) return null;
    RawDatagramSocket? sock;
    try {
      sock = await RawDatagramSocket.bind(addr.type == InternetAddressType.IPv6 ? InternetAddress.anyIPv6 : InternetAddress.anyIPv4, 0);
      final id = _rand.nextInt(0x10000);
      final name = 'monitor-${_rand.nextInt(1 << 32).toRadixString(16)}.example.com';
      final sw = Stopwatch()..start();
      sock.send(buildQuery(id, name), addr, port);
      final c = Completer<int?>();
      final timer = Timer(timeout, () {
        if (!c.isCompleted) c.complete(null);
      });
      sock.listen((e) {
        if (e != RawSocketEvent.read) return;
        final d = sock!.receive();
        if (d == null || c.isCompleted) return;
        final rcode = answerCode(d.data, id);
        if (rcode == null) return;
        c.complete(rcode == 0 || rcode == 3 ? sw.elapsedMilliseconds : null);
      });
      final r = await c.future;
      timer.cancel();
      return r;
    } catch (_) {
      return null;
    } finally {
      sock?.close();
    }
  }

  static Uint8List buildQuery(int id, String name) {
    final b = BytesBuilder()
      ..add([id >> 8, id & 0xff, 0x01, 0x00, 0, 1, 0, 0, 0, 0, 0, 0]);
    for (final label in name.split('.')) {
      b.addByte(label.length);
      b.add(label.codeUnits);
    }
    b.add([0, 0, 1, 0, 1]);
    return b.toBytes();
  }

  /// The response code of a DNS answer to query [id], or null if it isn't one.
  static int? answerCode(Uint8List d, int id) {
    if (d.length < 12 || (d[0] << 8 | d[1]) != id || d[2] & 0x80 == 0) return null;
    return d[3] & 0x0f;
  }

  static Future<(String, int, String)?> _lookup() async {
    final sw = Stopwatch()..start();
    try {
      final r = await InternetAddress.lookup('example.com').timeout(const Duration(seconds: 2));
      return r.isEmpty ? null : ('answers', sw.elapsedMilliseconds, 'system lookup');
    } catch (_) {
      return null;
    }
  }

  /// "eth0 192.168.1.5, tailscale0 100.64.0.3", IPv4 only, no loopback.
  static Future<String?> interfaces() async {
    try {
      final list = await NetworkInterface.list(type: InternetAddressType.IPv4);
      return [
        for (final i in list)
          for (final a in i.addresses)
            if (!a.isLoopback) '${i.name} ${a.address}',
      ].join(', ');
    } catch (_) {
      return null;
    }
  }
}
