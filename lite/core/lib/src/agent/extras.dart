import 'dart:convert';
import 'dart:io';
import 'dart:isolate';

import '../config.dart';
import 'containers.dart';
import 'netcheck.dart';

import '../report.dart';
import 'linux.dart' show linuxStatvfs;
import 'windows.dart' show windowsDrives, windowsRegString, windowsPipeRequest, windowsUsbDevices;

/// Readings beyond the basics, all through the open-ended `readings` list so
/// the app shows them without knowing them: what the machine is (read once),
/// temperatures, and how Tailscale and Syncthing are doing on it.
/// Every probe is read-only and gives up quietly when it can't read.
class Extras {
  List<Reading>? _system;
  List<Reading> _services = const [];
  DateTime? _servicesAt;

  Future<List<Reading>> collect(DateTime now) async {
    _system ??= _readSystem();
    if (_servicesAt == null || now.difference(_servicesAt!) >= const Duration(seconds: 55)) {
      _servicesAt = now;
      _services = [
        ...await _tailscale(),
        ...await _syncthing(),
        ...await _containers(),
        ..._windowsUsb(),
      ];
    }
    return [..._system!, ..._temps(), ..._drives(), ...await NetCheck.collect(), ..._services];
  }

  static String? _read(String p) {
    try {
      final s = File(p).readAsStringSync().trim();
      return s.isEmpty ? null : s;
    } catch (_) {
      return null;
    }
  }

  // ---- What the machine is ------------------------------------------------

  List<Reading> _readSystem() {
    Reading t(String k, String label, String? v) =>
        Reading(key: 'sys.$k', label: label, value: v ?? '', group: 'System');
    final out = <Reading>[];
    String? maker, model, cpu;
    if (Platform.isLinux) {
      maker = _read('/sys/class/dmi/id/sys_vendor');
      model = _read('/sys/class/dmi/id/product_name');
      final v = _read('/sys/class/dmi/id/product_version');
      if (model != null && v != null && v.length > 2 && !RegExp(r'^(none|to be filled|default|system version)', caseSensitive: false).hasMatch(v)) {
        model = '$model ($v)';
      }
      cpu = RegExp(r'^model name\s*:\s*(.+)$', multiLine: true).firstMatch(_read('/proc/cpuinfo') ?? '')?.group(1)?.trim();
    } else if (Platform.isWindows) {
      maker = windowsRegString(r'HARDWARE\DESCRIPTION\System\BIOS', 'SystemManufacturer');
      model = windowsRegString(r'HARDWARE\DESCRIPTION\System\BIOS', 'SystemProductName');
      cpu = windowsRegString(r'HARDWARE\DESCRIPTION\System\CentralProcessor\0', 'ProcessorNameString');
    }
    if (maker != null) out.add(t('maker', 'Maker', maker));
    if (model != null) out.add(t('model', 'Model', model));
    if (cpu != null) out.add(t('cpu', 'Processor', cpu.replaceAll(RegExp(r'\s+'), ' ')));
    out.add(Reading(key: 'sys.cores', label: 'Processor threads', value: '${Platform.numberOfProcessors}', group: 'System'));
    return out.where((r) => r.value != '').toList();
  }

  // ---- Every drive ------------------------------------------------------

  static const _realFs = {'ext4', 'ext3', 'ext2', 'btrfs', 'xfs', 'f2fs', 'vfat', 'exfat', 'ntfs', 'ntfs3', 'fuseblk', 'zfs'};

  List<Reading> _drives() {
    final found = <(String, int, int)>[];
    if (Platform.isWindows) {
      found.addAll(windowsDrives());
    } else if (Platform.isLinux && !File('/.dockerenv').existsSync()) {
      final seen = <String>{};
      for (final l in (_read('/proc/mounts') ?? '').split('\n')) {
        final f = l.split(' ');
        if (f.length < 3 || !_realFs.contains(f[2]) || !f[0].startsWith('/dev/')) continue;
        final mount = f[1].replaceAll(r'\040', ' ');
        if (mount.startsWith('/snap') || mount.startsWith('/var/lib/docker') || mount.startsWith('/boot/efi')) continue;
        final st = linuxStatvfs(mount);
        if (st == null || st.$1 < 1 << 30) continue; // under 1 GB: boot partitions and the like
        // Btrfs subvolumes share one pool: same size and free space, show once.
        if (!seen.add('${f[0]}:${st.$1}')) continue;
        found.add((mount, st.$1, st.$2));
      }
    }
    if (found.length < 2) return const []; // one drive is already "Storage"
    return [
      for (final (name, total, free) in found)
        Reading(
          key: 'drive.${name.replaceAll(RegExp(r'[^A-Za-z0-9]+'), '_')}',
          label: '$name (${_gb(total)})',
          value: (1000 * (total - free) / total).round() / 10,
          unit: '%',
          group: 'Drives',
          level: (total - free) / total >= 0.95 ? 'fault' : (total - free) / total >= 0.9 ? 'warn' : null,
        ),
    ];
  }

  static String _gb(int b) => b >= 1 << 40 ? '${(b / (1 << 40)).toStringAsFixed(1)} TB' : '${(b / (1 << 30)).round()} GB';

  // ---- Temperatures (Linux) -----------------------------------------------

  List<Reading> _temps() {
    if (!Platform.isLinux) return const [];
    // hwmon chips with well-known CPU sensor names first, then thermal zones.
    double? cpu;
    try {
      for (final d in Directory('/sys/class/hwmon').listSync()) {
        final name = _read('${d.path}/name') ?? '';
        if (!RegExp(r'^(coretemp|k10temp|zenpower|cpu_thermal|acpitz)$').hasMatch(name)) continue;
        final raw = _read('${d.path}/temp1_input');
        final v = raw == null ? null : int.tryParse(raw);
        if (v != null && v > 0) {
          cpu = v / 1000;
          if (name != 'acpitz') break;
        }
      }
    } catch (_) {}
    if (cpu == null) {
      try {
        for (final z in Directory('/sys/class/thermal').listSync().where((e) => e.path.contains('thermal_zone'))) {
          final type = _read('${z.path}/type') ?? '';
          final v = int.tryParse(_read('${z.path}/temp') ?? '');
          if (v == null || v <= 0) continue;
          if (type.contains('pkg') || type.contains('cpu') || type.contains('x86') || cpu == null) cpu = v / 1000;
        }
      } catch (_) {}
    }
    return [if (cpu != null) Reading(key: 'temp.cpu', label: 'Processor temperature', value: (cpu * 10).round() / 10, unit: '°C', group: 'Sensors')];
  }

  // ---- Tailscale (Linux: its own CLI, read-only) --------------------------

  /// Why the Windows local API could not be read (logged once, by the agent).
  static String? tailscaleProblem;

  Future<List<Reading>> _tailscale() async {
    if (!Platform.isLinux && !Platform.isWindows) return const [];
    try {
      Object? j;
      if (Platform.isWindows) {
        // No child processes on Windows (a console would flash): ask
        // tailscaled's local API over its named pipe, on a side isolate.
        final body = await Isolate.run(_windowsTailscaleStatus).timeout(const Duration(seconds: 4));
        j = jsonDecode(body);
        tailscaleProblem = null;
      } else {
        final r = await Process.run('tailscale', ['status', '--json']).timeout(const Duration(seconds: 4));
        if (r.exitCode != 0 && (r.stdout as String).isEmpty) return const [];
        j = jsonDecode(r.stdout as String);
      }
      if (j is! Map) return const [];
      final peers = j['Peer'] is Map ? (j['Peer'] as Map).values.whereType<Map>().toList() : const <Map>[];
      final online = peers.where((p) => p['Online'] == true).length;
      final exit = peers.where((p) => p['ExitNode'] == true).map((p) => p['HostName']).firstOrNull;
      return [
        Reading(key: 'ts.state', label: 'State', value: '${j['BackendState'] ?? 'unknown'}', group: 'Tailscale', level: j['BackendState'] == 'Running' ? null : 'fault'),
        Reading(key: 'ts.online', label: 'Devices online', value: online, group: 'Tailscale'),
        Reading(key: 'ts.devices', label: 'Devices in tailnet', value: peers.length, group: 'Tailscale'),
        if (exit != null) Reading(key: 'ts.exit', label: 'Exit node', value: '$exit', group: 'Tailscale'),
      ];
    } catch (e) {
      if (!Platform.isWindows) return const [];
      tailscaleProblem = '$e';
      // tailscaled can refuse its status API to a normal user's app (401 on
      // the laptop, 2026-11-27). The adapter still says whether the VPN is
      // up: a Tailscale interface holding a 100.64.0.0/10 address.
      final ip = await tailscaleAdapterIp();
      return [
        Reading(
            key: 'ts.state',
            label: 'State',
            value: ip == null ? 'Stopped' : 'Running',
            group: 'Tailscale',
            src: 'estimated',
            level: ip == null ? 'fault' : null,
            note: ip == null ? 'no Tailscale adapter with a 100.64/10 address' : 'from the Tailscale adapter ($ip); device counts need tailscaled\'s status, which it refused'),
      ];
    }
  }

  /// The address of a Tailscale network adapter (100.64.0.0/10), if any.
  static Future<String?> tailscaleAdapterIp() async {
    try {
      for (final i in await NetworkInterface.list(type: InternetAddressType.IPv4)) {
        if (!i.name.toLowerCase().contains('tailscale')) continue;
        for (final a in i.addresses) {
          final b = a.rawAddress;
          if (b.length == 4 && b[0] == 100 && b[1] >= 64 && b[1] < 128) return a.address;
        }
      }
    } catch (_) {}
    return null;
  }

  // ---- Docker containers (Linux: the Docker socket, read-only GET) -------

  Future<List<Reading>> _containers() async {
    if (!Platform.isLinux) return const [];
    try {
      final list = await Containers.dockerGet('/containers/json?all=1');
      if (list is! List) {
        final why = containersUnseen();
        return why == null ? const [] : [Reading(key: 'ct.access', label: 'Containers', value: 'not visible', group: 'Containers', src: 'unknown', note: why)];
      }
      var running = 0;
      String? pihole;
      final bad = <String>[];
      final stopped = <String>[];
      for (final c in list.whereType<Map>()) {
        final name = ((c['Names'] as List?)?.firstOrNull as String? ?? '?').replaceFirst('/', '');
        final state = '${c['State'] ?? ''}';
        final status = '${c['Status'] ?? ''}';
        if ('${c['Image'] ?? ''}'.contains('pihole')) {
          // Ask this host's Pi-hole directly: does DNS answer here?
          pihole = state != 'running' ? 'stopped' : (await NetCheck.dnsQuery('127.0.0.1') != null ? 'answers' : 'no answer');
        }
        if (state == 'running') {
          running++;
          if (status.contains('unhealthy')) bad.add('$name: unhealthy');
        } else if (state == 'restarting') {
          bad.add('$name: restarting');
        } else if (state == 'exited' && !status.startsWith('Exited (0)')) {
          bad.add('$name: ${status.split(' ago').first} ago');
        } else {
          stopped.add(name);
        }
      }
      return [
        Reading(key: 'ct.running', label: 'Running', value: running, group: 'Containers'),
        Reading(key: 'ct.total', label: 'Containers', value: list.length, group: 'Containers'),
        if (bad.isNotEmpty) Reading(key: 'ct.problems', label: 'Problems', value: bad.join('; '), group: 'Containers', level: 'warn'),
        if (pihole != null)
          // No level here: Pi-hole serves from one server at a time, so an
          // idle one is normal. Whether DNS works is judged across devices.
          Reading(key: 'net.pihole', label: 'Pi-hole DNS', value: pihole, group: 'Network', src: 'measured',
              note: pihole == 'answers' ? null : 'Pi-hole serves from one server at a time; this is only a problem if devices stop resolving names'),
        if (stopped.isNotEmpty) Reading(key: 'ct.stopped', label: 'Stopped', value: stopped.join(', '), group: 'Containers'),
      ];
    } catch (_) {
      return const [];
    }
  }

  /// Why this host's containers can't be seen, when Docker evidently runs
  /// here (its docker0 network exists) but the collector got no list; null
  /// when there is no Docker. Said once in the log, and as a reading, so a
  /// server whose containers never show says why instead of nothing.
  static String? containersUnseen({String? dockerHost, bool? dockerHere, bool? socket}) {
    dockerHere ??= Directory('/sys/class/net/docker0').existsSync();
    if (!dockerHere) return null;
    dockerHost ??= Platform.environment['DOCKER_HOST'] ?? '';
    socket ??= FileSystemEntity.typeSync('/var/run/docker.sock') != FileSystemEntityType.notFound;
    if (dockerHost.startsWith('tcp://')) {
      return 'Docker runs here, but the docker-list helper at ${dockerHost.replaceFirst('tcp://', '')} does not answer. Is the monitor-app-docker-list container running?';
    }
    if (socket) return 'Docker runs here, but its socket did not answer.';
    return 'Docker runs here, but the collector has no read-only way to see it: the docker-list helper is not set up in this server\'s compose file (Containers in packaging/docker/README.md).';
  }

  // ---- Windows USB devices (SetupAPI) ---------------------------------------

  /// A marker file is written before each scan and removed after it. If the
  /// collector finds it at start, the last scan took the process down, and
  /// USB stays off on this machine (the log says so) instead of crashing
  /// every minute.
  bool? _usbOk;
  List<Reading> _windowsUsb() {
    if (!Platform.isWindows) return const [];
    final marker = File('${LocalConfig.dir}${Platform.pathSeparator}usb-scan.running');
    _usbOk ??= !marker.existsSync();
    if (_usbOk == false) {
      return [Reading(key: 'usb.count', label: 'USB devices', value: null, group: 'USB', src: 'unknown', note: 'USB listing is off: it stopped the collector once (delete usb-scan.running in the Monitor settings folder to try again)')];
    }
    try {
      marker.writeAsStringSync('${DateTime.now().toUtc()}', flush: true);
      final list = windowsUsbDevices();
      marker.deleteSync();
      final names = [for (final u in list) '${u.name} (${u.vid}:${u.pid}${u.serial == null ? '' : ', ${u.serial}'})']..sort();
      return [
        Reading(key: 'usb.count', label: 'USB devices', value: list.length, group: 'USB', src: 'measured'),
        if (names.isNotEmpty) Reading(key: 'usb.list', label: 'Connected', value: names.join('; '), group: 'USB', src: 'measured'),
      ];
    } catch (e) {
      try {
        marker.deleteSync();
      } catch (_) {}
      return [Reading(key: 'usb.count', label: 'USB devices', value: null, group: 'USB', src: 'unknown', note: 'could not list: $e')];
    }
  }

  // ---- Syncthing (its local API, with the key from its own config) --------

  (String, String, List<(String, String)>)? _stConfig() {
    final env = Platform.environment;
    final home = env['HOME'] ?? env['USERPROFILE'] ?? '';
    final candidates = Platform.isWindows
        ? ['${env['LOCALAPPDATA']}\\Syncthing\\config.xml']
        : ['$home/.local/state/syncthing/config.xml', '$home/.config/syncthing/config.xml'];
    for (final p in candidates) {
      final x = _read(p);
      if (x == null) continue;
      final key = RegExp(r'<apikey>([^<]+)</apikey>').firstMatch(x)?.group(1);
      final gui = RegExp(r'<gui[^>]*>[\s\S]*?<address>([^<]+)</address>').firstMatch(x)?.group(1) ?? '127.0.0.1:8384';
      final tls = RegExp(r'<gui[^>]*tls="true"').hasMatch(x);
      final folders = [
        for (final m in RegExp(r'<folder\s[^>]*>').allMatches(x))
          (
            RegExp(r'\sid="([^"]*)"').firstMatch(m.group(0)!)?.group(1) ?? '',
            RegExp(r'\slabel="([^"]*)"').firstMatch(m.group(0)!)?.group(1) ?? '',
          ),
      ].where((f) => f.$1.isNotEmpty).toList();
      if (key == null) continue;
      final addr = gui.replaceFirst('0.0.0.0', '127.0.0.1');
      return ('${tls ? 'https' : 'http'}://$addr', key, folders);
    }
    return null;
  }

  Future<List<Reading>> _syncthing() async {
    final c = _stConfig();
    if (c == null) return const [];
    final (base, key, folders) = c;
    final http = HttpClient()
      ..connectionTimeout = const Duration(seconds: 2)
      ..badCertificateCallback = (cert, host, port) => host == '127.0.0.1' || host == 'localhost';
    Future<Object?> get(String path) async {
      final req = await http.getUrl(Uri.parse('$base$path')).timeout(const Duration(seconds: 3));
      req.headers.set('X-API-Key', key);
      final res = await req.close().timeout(const Duration(seconds: 3));
      final body = await res.transform(utf8.decoder).join().timeout(const Duration(seconds: 3));
      return res.statusCode == 200 ? jsonDecode(body) : null;
    }

    try {
      final out = <Reading>[];
      final conns = await get('/rest/system/connections');
      if (conns is Map && conns['connections'] is Map) {
        final all = (conns['connections'] as Map).values.whereType<Map>().toList();
        out.add(Reading(key: 'st.connected', label: 'Devices connected', value: all.where((x) => x['connected'] == true).length, group: 'Syncthing'));
      }
      var upToDate = 0;
      final problems = <String>[];
      for (final (id, label) in folders) {
        final s = await get('/rest/db/status?folder=${Uri.encodeQueryComponent(id)}');
        if (s is! Map) continue;
        final state = '${s['state'] ?? ''}';
        final need = (s['needFiles'] as num?)?.toInt() ?? 0;
        final errors = (s['errors'] as num?)?.toInt() ?? 0;
        final name = label.isNotEmpty ? label : id;
        if (state == 'idle' && need == 0 && errors == 0) {
          upToDate++;
        } else {
          problems.add('$name: ${state.isEmpty ? 'unknown' : state}${need > 0 ? ', $need files to get' : ''}${errors > 0 ? ', $errors errors' : ''}');
        }
      }
      if (folders.isNotEmpty) {
        out.add(Reading(key: 'st.folders', label: 'Folders', value: folders.length, group: 'Syncthing'));
        out.add(Reading(key: 'st.uptodate', label: 'Folders up to date', value: upToDate, group: 'Syncthing'));
        if (problems.isNotEmpty) out.add(Reading(key: 'st.problems', label: 'Not up to date', value: problems.join('; '), group: 'Syncthing', level: 'warn'));
      }
      return out;
    } catch (_) {
      return const [];
    } finally {
      http.close(force: true);
    }
  }
}

/// GET /localapi/v0/status from tailscaled on Windows, over its named pipe
/// (the same one the tailscale CLI uses). Returns the JSON body.
String _windowsTailscaleStatus() => httpBody(windowsPipeRequest(r'\\.\pipe\ProtectedPrefix\Administrators\Tailscale\tailscaled',
    utf8.encode('GET /localapi/v0/status HTTP/1.1\r\nHost: local-tailscaled.sock\r\nConnection: close\r\n\r\n')));

/// The body of a raw HTTP/1.1 response (status 200 only; chunked or not).
String httpBody(List<int> raw) {
  final text = utf8.decode(raw, allowMalformed: true);
  final split = text.indexOf('\r\n\r\n');
  if (split < 0) throw const FormatException('no HTTP header');
  final head = text.substring(0, split).toLowerCase();
  final status = head.split('\r\n').first;
  if (!status.contains(' 200')) throw FormatException('tailscaled answered "$status"');
  var body = text.substring(split + 4);
  if (head.contains('transfer-encoding: chunked')) {
    final out = StringBuffer();
    var i = 0;
    while (i < body.length) {
      final eol = body.indexOf('\r\n', i);
      if (eol < 0) break;
      final size = int.parse(body.substring(i, eol).split(';').first.trim(), radix: 16);
      if (size == 0) break;
      out.write(body.substring(eol + 2, eol + 2 + size));
      i = eol + 2 + size + 2;
    }
    body = out.toString();
  }
  return body;
}
