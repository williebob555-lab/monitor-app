import 'dart:convert';
import 'dart:io';

import '../report.dart';
import 'windows.dart' show windowsRegString;

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
      ];
    }
    return [..._system!, ..._temps(), ..._services];
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

  Future<List<Reading>> _tailscale() async {
    if (!Platform.isLinux) return const [];
    try {
      final r = await Process.run('tailscale', ['status', '--json']).timeout(const Duration(seconds: 4));
      if (r.exitCode != 0 && (r.stdout as String).isEmpty) return const [];
      final j = jsonDecode(r.stdout as String);
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
    } catch (_) {
      return const [];
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
