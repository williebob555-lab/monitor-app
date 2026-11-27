import 'dart:io';

import '../report.dart';

/// What UPower knows about power devices, read without any privilege:
/// natively with `upower -d` (the system D-Bus, as any user may), or in a
/// container from the small read-only file the host's exporter writes
/// (packaging/linux/upower-export.sh). Monitor App only ever reads UPower:
/// it never changes its settings, and the host's own low-battery action
/// (UPower's CriticalPowerAction) stays the only shutdown path.
class Upower {
  Upower({required this.devices, this.daemon = const {}, this.config = const {}, this.policyFiles = const [], this.errors = const [], this.exported, required this.from});

  /// Every device UPower listed, each with its kind (ups, battery, mouse...).
  final List<UpowerDevice> devices;

  /// The daemon's own block: daemon-version, on-battery, critical-action.
  final Map<String, String> daemon;

  /// Policy lines from the host's /etc/UPower/UPower.conf (PercentageAction,
  /// UsePercentageForPolicy, ...), as the host has them.
  final Map<String, String> config;

  /// The policy files that set any of [config], in the order UPower
  /// applies them (UPower.conf, then UPower.conf.d/*.conf by name).
  final List<String> policyFiles;

  /// What the exporter could not get ("UPower did not answer").
  final List<String> errors;

  /// When the host exported it (file only).
  final DateTime? exported;

  /// "upower" (asked here) or "upower-export" (the host's exported file).
  final String from;

  /// Only uninterruptible power supplies: never a laptop's own battery, a
  /// mouse, a keyboard, a phone or the combined "DisplayDevice".
  List<UpowerDevice> get ups => [
        for (final d in devices)
          if (d.kind == 'ups' && !d.path.endsWith('/DisplayDevice')) d
      ];

  /// Parses `upower -d`, or the exporter's file (the same text with an
  /// "exported:" line and "config.Key:" lines; files from 2.6.5 hold a
  /// single `upower -i` block with no "Device:" line, which still reads).
  static Upower parse(String text, {required String from}) {
    final devices = <UpowerDevice>[];
    final daemon = <String, String>{}, config = <String, String>{};
    final files = <String>[], errors = <String>[];
    int? exported;
    Map<String, String>? props;
    String? path, kind;
    var inDaemon = false, inHistory = false;
    void flush() {
      if (props != null && (props!.isNotEmpty || kind != null)) devices.add(UpowerDevice(path ?? '', _kindOf(path, kind), props!));
      props = null;
      path = kind = null;
    }

    for (final raw in text.split('\n')) {
      final line = raw.trimRight();
      if (line.trim().isEmpty) continue;
      final indented = line.startsWith(' ') || line.startsWith('\t');
      if (!indented) {
        inHistory = false;
        if (line.startsWith('Device:')) {
          flush();
          inDaemon = false;
          path = line.substring(7).trim();
          props = {};
        } else if (line.startsWith('Daemon:')) {
          flush();
          inDaemon = true;
        } else if (line.startsWith('exported:')) {
          exported = int.tryParse(line.substring(9).trim());
        } else if (line.startsWith('error:')) {
          errors.add(line.substring(6).trim());
        } else if (line.startsWith('policy-file:')) {
          files.add(line.substring(12).trim());
        } else if (line.startsWith('config.')) {
          final k = line.indexOf(':');
          if (k > 7) config[line.substring(7, k).trim()] = line.substring(k + 1).trim();
        }
        continue;
      }
      final t = line.trim();
      if (t.startsWith('History') || t.startsWith('Statistics')) {
        inHistory = true;
        continue;
      }
      if (inHistory) continue;
      final k = t.indexOf(':');
      if (k < 0) {
        // The kind line: a lone lowercase word ("ups", "battery", "mouse").
        if (!inDaemon && RegExp(r'^[a-z][a-z-]*$').hasMatch(t)) {
          props ??= {};
          kind ??= t;
        }
        continue;
      }
      final key = t.substring(0, k).trim(), value = t.substring(k + 1).trim();
      if (inDaemon) {
        daemon[key] = value;
      } else {
        (props ??= {})[key] = value;
      }
    }
    flush();
    return Upower(
        devices: devices,
        daemon: daemon,
        config: config,
        policyFiles: files,
        errors: errors,
        exported: exported == null ? null : DateTime.fromMillisecondsSinceEpoch(exported * 1000, isUtc: true),
        from: from);
  }

  static String _kindOf(String? path, String? kind) {
    if (kind != null) return kind;
    final m = RegExp(r'/devices/([a-z-]+)_').firstMatch(path ?? '');
    return m?.group(1) ?? 'unknown';
  }

  /// Asks UPower on this machine (`upower -d`), or null when there is no
  /// upower command or no system D-Bus (as inside a container).
  static Future<Upower?> ask() async {
    try {
      final r = await Process.run('upower', ['-d'], environment: const {'LC_ALL': 'C', 'LANG': 'C'}).timeout(const Duration(seconds: 3));
      if (r.exitCode != 0) return null;
      final u = parse(r.stdout as String, from: 'upower');
      if (u.daemon.isEmpty && u.devices.isEmpty) return null;
      final (config, files) = readConfig();
      return Upower(devices: u.devices, daemon: u.daemon, config: config, policyFiles: files, from: 'upower');
    } catch (_) {
      return null;
    }
  }

  /// The host's UPower policy (read-only; never written), resolved the way
  /// UPower does: UPower.conf, then UPower.conf.d/*.conf in name order, a
  /// later file's key winning; only keys in the [UPower] group count.
  /// Returns the effective values and the files that set any of them.
  static (Map<String, String>, List<String>) readConfig([String dir = '/etc/UPower']) {
    final out = <String, String>{}, used = <String>[];
    final files = <String>['$dir/UPower.conf'];
    try {
      final d = [
        for (final e in Directory('$dir/UPower.conf.d').listSync())
          if (e is File && e.path.endsWith('.conf')) e.path
      ];
      d.sort(_byteCompare); // name order by bytes, as the C library sorts
      files.addAll(d);
    } catch (_) {}
    final key = RegExp(r'^\s*(UsePercentageForPolicy|PercentageLow|PercentageCritical|PercentageAction|TimeLow|TimeCritical|TimeAction|CriticalPowerAction)\s*=\s*([^\s#;]+)');
    for (final f in files) {
      try {
        String? group;
        var named = false;
        for (final l in File(f).readAsLinesSync()) {
          final g = RegExp(r'^\s*\[([^\]]*)\]').firstMatch(l);
          if (g != null) {
            group = g.group(1)!.trim();
            continue;
          }
          final m = key.firstMatch(l);
          if (m == null || group != 'UPower') continue;
          if (!named) used.add(f);
          named = true;
          out[m.group(1)!] = m.group(2)!;
        }
      } catch (_) {}
    }
    return (out, used);
  }

  static int _byteCompare(String a, String b) {
    final x = a.codeUnits, y = b.codeUnits;
    for (var i = 0; i < x.length && i < y.length; i++) {
      if (x[i] != y[i]) return x[i] - y[i];
    }
    return x.length - y.length;
  }

  /// Where a host-side exporter leaves UPower's details, mounted read-only
  /// into the container. The collector gets this one file: no D-Bus, no
  /// USB device.
  static const exportFile = '/host/upower/ups.txt';

  /// The exported details, or why they can't be used (missing file: null).
  /// An export older than 3 minutes is not shown as current.
  static (Upower?, String?)? readExport(String path, DateTime now) {
    final f = File(path);
    try {
      if (!f.existsSync() || f.lengthSync() > 64 * 1024) return null;
      final u = parse(f.readAsStringSync(), from: 'upower-export');
      if (u.exported == null) return (null, 'the UPower export has no "exported" time');
      final age = now.difference(u.exported!).inSeconds;
      if (age > 180) return (null, 'the UPower export is ${(age / 60).round()} min old (is its timer running on the host?)');
      // UPower failed on the host: unknown with its reason, never old numbers.
      if (u.errors.isNotEmpty) return (null, 'UPower on the host: ${u.errors.first}');
      if (u.ups.isEmpty) return (null, 'UPower on the host sees no UPS');
      return (u, null);
    } catch (e) {
      return (null, 'the UPower export could not be read: $e');
    }
  }

  /// UPower's UPS for the USB device at [usbPath] ("1-5"), and how it was
  /// matched. Exact first: the same serial number, or UPower's device
  /// sitting on that USB port; then the same maker and model; then, when
  /// both sides know of exactly one UPS, that one.
  (UpowerDevice, String)? match({required String usbPath, String? serial, String? maker, String? product, required int usbUpsCount, String sys = '/sys'}) {
    final all = ups;
    String norm(String? s) => (s ?? '').trim().toLowerCase();
    if (norm(serial).isNotEmpty) {
      for (final d in all) {
        if (norm(d.props['serial']) == norm(serial)) return (d, 'same serial number');
      }
    }
    for (final d in all) {
      if (d.usbPath(sys) == usbPath) return (d, 'same USB port');
    }
    for (final d in all) {
      if (norm(maker).isNotEmpty && norm(d.props['vendor']) == norm(maker) && norm(d.props['model']) == norm(product)) return (d, 'same maker and model');
    }
    if (all.length == 1 && usbUpsCount == 1) return (all.first, 'the only UPS on this machine');
    return null;
  }

  /// UPS readings from UPower. UPower writes 0 for anything it does not
  /// know (and `upower` then leaves the line out), so a missing or zero
  /// time is unknown, never "0 min", and a 0 % charge counts only while
  /// UPower knows the state.
  List<Reading> readings(UpowerDevice d) {
    final p = d.props;
    final present = p['present'] != 'no';
    final state = present ? (p['state'] ?? 'unknown') : 'unknown';
    final known = state != 'unknown';
    final onBattery = state == 'discharging' || state == 'empty';
    final low = onBattery && const {'low', 'critical', 'action'}.contains(p['warning-level']);
    var pct = double.tryParse((p['percentage'] ?? '').replaceAll('%', '').trim());
    if (pct == 0 && !known) pct = null;
    final runtime = _minutes(p['time to empty']);
    final why = !present
        ? 'UPower says the UPS is not present'
        : known
            ? 'UPower does not report it for this UPS'
            : 'UPower does not know the state yet';
    final note = from == 'upower-export' ? 'host UPower, through its exported file' : 'UPower on this machine';
    Reading r(String key, String label, Object? value, {String? unit, String src = 'measured', String? n, String? level}) => Reading(
        key: key,
        label: label,
        value: value is double ? (value * 10).round() / 10 : value,
        unit: value == null ? null : unit,
        group: 'Power',
        src: value == null ? 'unknown' : src,
        note: value == null ? why : n,
        level: level,
        from: from);
    return [
      r(
          'ups.status',
          'Running on',
          !known
              ? null
              : low
                  ? 'battery, low'
                  : onBattery
                      ? 'battery'
                      : 'mains',
          level: low
              ? 'fault'
              : onBattery
                  ? 'warn'
                  : null),
      r('ups.charge', 'Battery charge', pct,
          unit: '%',
          level: pct != null && pct < 20
              ? 'fault'
              : pct != null && pct < 50 && onBattery
                  ? 'warn'
                  : null),
      r('ups.runtime', 'Runtime left', runtime, unit: 'min', src: 'estimated', n: "the UPS's own estimate, through $note"),
      if (p['vendor'] != null) r('ups.maker', 'Maker', p['vendor'], src: 'declared'),
      if (p['model'] != null) r('ups.model', 'Model', p['model'], src: 'declared'),
      if ((p['serial'] ?? '').isNotEmpty) r('ups.serial', 'Serial number', p['serial'], src: 'declared'),
      if (hostAction != null)
        r('ups.hostAction', 'Host protection', hostAction,
            src: 'declared', n: 'UPower on the host does this${policyFiles.isEmpty ? '' : ' (set in ${policyFiles.last})'}; Monitor App only reads it and never changes it'),
    ];
  }

  /// What the host does when the UPS runs low, in words ("powers off at
  /// 20 %"), or null when UPower does not say.
  String? get hostAction {
    final a = daemon['critical-action'] ?? config['CriticalPowerAction'];
    if (a == null) return null;
    final verb = switch (a) {
      'PowerOff' => 'powers off',
      'Hibernate' => 'hibernates',
      'HybridSleep' => 'hybrid-sleeps',
      'Suspend' => 'suspends',
      'Ignore' => 'does nothing',
      _ => a,
    };
    if (a == 'Ignore') return 'host does nothing when the battery runs low';
    final byPct = (config['UsePercentageForPolicy'] ?? 'true').toLowerCase() != 'false';
    String pct(String k) => '${config[k]} %';
    String min(String k) => '${(int.tryParse(config[k]!) ?? 0) ~/ 60} min left';
    final at = byPct ? (config['PercentageAction'] == null ? '' : ' at ${pct('PercentageAction')}') : (config['TimeAction'] == null ? '' : ' with ${min('TimeAction')}');
    final low = byPct ? 'PercentageLow' : 'TimeLow', critical = byPct ? 'PercentageCritical' : 'TimeCritical';
    final warn = [
      if (config[low] != null) 'low at ${byPct ? pct(low) : min(low)}',
      if (config[critical] != null) 'critical at ${byPct ? pct(critical) : min(critical)}',
    ];
    return 'host $verb$at${warn.isEmpty ? '' : ' (${warn.join(', ')})'}';
  }

  static double? _minutes(String? s) {
    if (s == null) return null;
    final m = RegExp(r'([\d.,]+)\s*(hours?|minutes?|seconds?)').firstMatch(s);
    if (m == null) return null;
    final x = double.tryParse(m.group(1)!.replaceAll(',', '.'));
    if (x == null || x <= 0) return null;
    final u = m.group(2)!;
    return u.startsWith('hour')
        ? x * 60
        : u.startsWith('second')
            ? x / 60
            : x;
  }
}

class UpowerDevice {
  UpowerDevice(this.path, this.kind, this.props);
  final String path, kind;
  final Map<String, String> props;

  /// The USB port ("1-5") UPower's device sits on: from a full sysfs
  /// native-path, or by following hiddevN in sysfs (Docker shows the host's
  /// sysfs read-only, so this works in a container too).
  String? usbPath([String sys = '/sys']) {
    var p = props['native-path'] ?? '';
    if (RegExp(r'^hiddev\d+$').hasMatch(p)) {
      try {
        p = Directory('$sys/class/usbmisc/$p/device').resolveSymbolicLinksSync();
      } catch (_) {
        return null;
      }
    }
    // .../usb1/1-5/1-5:1.0/usbmisc/hiddev0 -> 1-5
    final m = RegExp(r'/(\d+-[\d.]+):\d+\.\d+(/|$)').firstMatch(p);
    return m?.group(1);
  }
}
