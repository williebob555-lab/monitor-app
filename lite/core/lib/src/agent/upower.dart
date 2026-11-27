import 'dart:io';

import '../report.dart';

/// What UPower knows about power devices, read without any privilege:
/// natively with `upower -d` (the system D-Bus, as any user may), or in a
/// container from the small read-only file the host's exporter writes
/// (packaging/linux/upower-export.sh). Monitor App only ever reads UPower:
/// it never changes its settings, and the host's own low-battery action
/// (UPower's CriticalPowerAction) stays the only shutdown path.
class Upower {
  Upower({required this.devices, this.daemon = const {}, this.config = const {}, this.exported, required this.from});

  /// Every device UPower listed, each with its kind (ups, battery, mouse...).
  final List<UpowerDevice> devices;

  /// The daemon's own block: daemon-version, on-battery, critical-action.
  final Map<String, String> daemon;

  /// Policy lines from the host's /etc/UPower/UPower.conf (PercentageAction,
  /// UsePercentageForPolicy, ...), as the host has them.
  final Map<String, String> config;

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
    return Upower(devices: devices, daemon: daemon, config: config, exported: exported == null ? null : DateTime.fromMillisecondsSinceEpoch(exported * 1000, isUtc: true), from: from);
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
      final r = await Process.run('upower', ['-d']).timeout(const Duration(seconds: 3));
      if (r.exitCode != 0) return null;
      final u = parse(r.stdout as String, from: 'upower');
      if (u.daemon.isEmpty && u.devices.isEmpty) return null;
      return Upower(devices: u.devices, daemon: u.daemon, config: readConfig(), from: 'upower');
    } catch (_) {
      return null;
    }
  }

  /// The host's UPower policy (read-only; never written): UPower.conf, then
  /// any UPower.conf.d/*.conf, later files winning as they do for UPower.
  static Map<String, String> readConfig([String dir = '/etc/UPower']) {
    final out = <String, String>{};
    final files = <String>['$dir/UPower.conf'];
    try {
      files.addAll([
        for (final e in Directory('$dir/UPower.conf.d').listSync())
          if (e.path.endsWith('.conf')) e.path
      ]..sort());
    } catch (_) {}
    for (final f in files) {
      try {
        for (final l in File(f).readAsLinesSync()) {
          final m = RegExp(r'^\s*(UsePercentageForPolicy|PercentageLow|PercentageCritical|PercentageAction|TimeLow|TimeCritical|TimeAction|CriticalPowerAction)\s*=\s*(\S+)').firstMatch(l);
          if (m != null) out[m.group(1)!] = m.group(2)!;
        }
      } catch (_) {}
    }
    return out;
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
      if (hostAction != null) r('ups.hostAction', 'Host protection', hostAction, src: 'declared', n: 'set in UPower on the host, which shuts it down; Monitor App only reads it'),
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
    final at = byPct
        ? (config['PercentageAction'] == null ? '' : ' at ${config['PercentageAction']} %')
        : (config['TimeAction'] == null ? '' : ' with ${(int.tryParse(config['TimeAction']!) ?? 0) ~/ 60} min left');
    return 'host $verb$at';
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
