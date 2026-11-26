import 'dart:async';
import 'dart:convert';
import 'dart:io';

import '../report.dart';
import '../topology.dart';

/// Docker containers on this host, as attached things grouped by Compose
/// project: host -> project -> container.
///
/// Read-only. It talks to Docker through DOCKER_HOST=tcp://127.0.0.1:2375,
/// meant to be a socket proxy that allows only GET on /containers (see
/// packaging/docker/compose.yaml), or else /var/run/docker.sock if this
/// program was given it. It only lists, inspects nothing it can't read, and
/// asks each running container for one stats sample.
class Containers {
  List<Report> _last = const [];
  DateTime? _at;

  Future<List<Report>> collect(String hostId, String? machine, String? hostIp, DateTime now, int next) async {
    if (!Platform.isLinux) return const [];
    if (_at != null && now.difference(_at!) < const Duration(seconds: 55)) {
      return [for (final r in _last) _retime(r, now, next)];
    }
    _at = now;
    final list = await dockerGet('/containers/json?all=1');
    if (list is! List) return _last = const [];
    final byProject = <String, List<Report>>{};
    await Future.wait(list.whereType<Map>().map((c) async {
      final labels = (c['Labels'] as Map?)?.cast<String, Object?>() ?? const {};
      final project = '${labels['com.docker.compose.project'] ?? 'standalone'}';
      final name = ((c['Names'] as List?)?.firstOrNull as String? ?? '?').replaceFirst('/', '');
      final state = '${c['State'] ?? ''}';
      final status = '${c['Status'] ?? ''}';
      final health = RegExp(r'\((healthy|unhealthy|health: starting)\)').firstMatch(status)?.group(1);
      final pid = _projectId(hostId, project);
      final id = 'ctr-${_slug(hostId)}-${_slug(name)}';
      Map? stats;
      if (state == 'running') stats = await dockerGet('/containers/${c['Id']}/stats?stream=false') as Map?;
      final url = await _url(labels, c['Ports'] as List?, hostIp);
      final level = state == 'restarting'
          ? 'fault'
          : state == 'exited' && !status.startsWith('Exited (0)')
              ? 'warn'
              : null;
      final r = Report(
        id: id,
        name: '${labels['com.docker.compose.service'] ?? name}',
        kind: 'thing',
        time: now,
        machine: machine,
        nextSeconds: next,
        connected: true,
        node: NodeDecl(id: id, type: 'container', parent: pid),
        readings: [
          Reading(key: 'ct.state', label: 'State', value: state, group: 'Container', src: 'measured', level: level),
          Reading(key: 'ct.status', label: 'Docker says', value: status, group: 'Container', src: 'measured'),
          if (health != null)
            Reading(key: 'ct.health', label: 'Health check', value: health, group: 'Container', src: 'measured', level: health == 'unhealthy' ? 'fault' : null),
          Reading(key: 'ct.cpu', label: 'CPU', value: _cpu(stats), unit: '%', group: 'Container', src: _cpu(stats) == null ? 'unknown' : 'measured',
              note: _cpu(stats) == null ? (state == 'running' ? 'Docker gave no sample' : 'not running') : null),
          Reading(key: 'ct.mem', label: 'Memory', value: _mem(stats), unit: 'MB', group: 'Container', src: _mem(stats) == null ? 'unknown' : 'measured',
              note: _mem(stats) == null ? (state == 'running' ? 'Docker gave no sample' : 'not running') : null),
          Reading(key: 'ct.image', label: 'Image', value: '${c['Image'] ?? '?'}', group: 'Container', src: 'declared'),
          Reading(key: 'ct.name', label: 'Container', value: name, group: 'Container', src: 'declared'),
          if (url != null) Reading(key: 'ct.url', label: 'Open', value: url.$1, group: 'Interface', src: url.$2, note: url.$3),
        ],
      );
      (byProject[project] ??= []).add(r);
    }));
    final out = <Report>[];
    for (final e in byProject.entries) {
      final cs = e.value..sort((a, b) => a.name.compareTo(b.name));
      final running = cs.where((c) => c.readings.first.value == 'running').length;
      final bad = [
        for (final c in cs)
          for (final x in c.readings)
            if (x.level != null) '${c.name}: ${x.value}',
      ];
      final pid = _projectId(hostId, e.key);
      out.add(Report(
        id: pid,
        name: e.key,
        kind: 'thing',
        time: now,
        machine: machine,
        nextSeconds: next,
        connected: true,
        node: NodeDecl(id: pid, type: 'compose-project', parent: hostId, children: [for (final c in cs) c.id], links: [
          NodeLink(to: hostId, via: 'docker', dir: 'both', detail: 'Docker on this host'),
        ]),
        readings: [
          if (bad.isNotEmpty) Reading(key: 'cp.problems', label: 'Needs a look', value: bad.join(', '), group: 'Project', src: 'measured', level: 'fault'),
          Reading(key: 'cp.running', label: 'Running', value: '$running of ${cs.length}', group: 'Project', src: 'measured',
              level: running < cs.length ? 'warn' : null),
        ],
      ));
      out.addAll(cs);
    }
    return _last = out;
  }

  static String _slug(String s) => s.toLowerCase().replaceAll(RegExp(r'[^a-z0-9]+'), '-');
  static String _projectId(String host, String project) => 'compose-${_slug(host)}-${_slug(project)}';

  static Report _retime(Report r, DateTime now, int next) => Report.tryParse({...r.toJson(), 't': now.millisecondsSinceEpoch ~/ 1000, 'next': next}) ?? r;

  static double? _cpu(Map? s) {
    try {
      final cpu = s!['cpu_stats'] as Map, pre = s['precpu_stats'] as Map;
      final d = (cpu['cpu_usage']['total_usage'] as num) - (pre['cpu_usage']['total_usage'] as num);
      final sys = (cpu['system_cpu_usage'] as num) - (pre['system_cpu_usage'] as num);
      final n = (cpu['online_cpus'] as num?) ?? 1;
      if (sys <= 0) return null;
      return double.parse((d / sys * n * 100).toStringAsFixed(1));
    } catch (_) {
      return null;
    }
  }

  static double? _mem(Map? s) {
    try {
      final m = s!['memory_stats'] as Map;
      final cache = ((m['stats'] as Map?)?['inactive_file'] as num?) ?? 0;
      return double.parse((((m['usage'] as num) - cache) / (1024 * 1024)).toStringAsFixed(1));
    } catch (_) {
      return null;
    }
  }

  /// The container's web interface: a monitor.url label wins; otherwise its
  /// first published TCP port, but only if something answers on it here.
  /// Returns (url, src, note).
  static Future<(String, String, String?)?> _url(Map<String, Object?> labels, List? ports, String? hostIp) async {
    final own = labels['monitor.url'];
    if (own is String && own.startsWith(RegExp('https?://'))) return (own, 'declared', 'from its monitor.url label');
    for (final p in ports ?? const []) {
      if (p is! Map || p['Type'] != 'tcp' || p['PublicPort'] == null) continue;
      final ip = '${p['IP'] ?? ''}';
      if (ip.contains(':') && ip != '::') continue; // other IPv6 binds
      final port = p['PublicPort'] as int;
      final local = ip == '0.0.0.0' || ip == '::' || ip.isEmpty ? '127.0.0.1' : ip;
      try {
        final s = await Socket.connect(local, port, timeout: const Duration(milliseconds: 600));
        s.destroy();
      } catch (_) {
        continue;
      }
      final where = ip == '0.0.0.0' || ip == '::' || ip.isEmpty ? (hostIp ?? local) : ip;
      final tls = const {443, 8443, 9443}.contains(p['PrivatePort']);
      return ('${tls ? 'https' : 'http'}://$where:$port', 'measured', 'port $port answers on this host');
    }
    return null;
  }

  /// One GET against the Docker API: proxy over TCP, or the socket.
  static Future<Object?> dockerGet(String path) async {
    Socket? sock;
    try {
      final env = Platform.environment['DOCKER_HOST'] ?? '';
      if (env.startsWith('tcp://')) {
        final u = Uri.parse(env.replaceFirst('tcp://', 'http://'));
        sock = await Socket.connect(u.host, u.port, timeout: const Duration(seconds: 2));
      } else {
        const p = '/var/run/docker.sock';
        if (FileSystemEntity.typeSync(p) == FileSystemEntityType.notFound) return null;
        sock = await Socket.connect(InternetAddress(p, type: InternetAddressType.unix), 0).timeout(const Duration(seconds: 2));
      }
      sock.write('GET $path HTTP/1.0\r\nHost: docker\r\n\r\n');
      await sock.flush();
      final raw = await sock.cast<List<int>>().transform(utf8.decoder).join().timeout(const Duration(seconds: 5));
      final cut = raw.indexOf('\r\n\r\n');
      if (cut < 0 || !raw.substring(0, raw.indexOf('\r\n')).contains(' 200')) return null;
      return jsonDecode(raw.substring(cut + 4));
    } catch (_) {
      return null;
    } finally {
      sock?.destroy();
    }
  }
}
