import 'dart:async';
import 'dart:convert';
import 'dart:io';

import '../report.dart';
import 'hid_ups.dart';
import '../topology.dart';

/// Things attached to this computer that get their own node under it: a UPS
/// on USB and USB audio interfaces (Linux). Everything comes from what the
/// system already exposes: sysfs for USB, /proc/asound for ALSA, and for a
/// UPS the open NUT server (usbhid-ups) or upower. No vendor software.
///
/// Every reading says where it comes from (Reading.src): measured now,
/// declared by the device about itself, estimated, or unknown with a note
/// saying why. Nothing is filled in.
/// Docker hides /proc/asound; the container can be given the host's copy
/// at /host/asound (see packaging/docker/compose.yaml).
final String _asound = Directory('/host/asound').existsSync() ? '/host/asound' : '/proc/asound';

class Things {
  /// USB vendors that make UPSes speaking the USB HID power device class.
  static const upsVendors = {
    '0764': 'CyberPower',
    '051d': 'APC',
    '0463': 'Eaton',
    '09ae': 'Tripp Lite',
    '06da': 'Phoenixtec',
    '0665': 'Cypress (UPS)',
  };

  final _devnum = <String, String>{};
  final _reconnects = <String, int>{};
  List<_Found> _last = const [];
  DateTime? _at;

  /// Reads the attached things (at most once every 55 s; between reads the
  /// last result is reused). Returns one report per thing, with its node
  /// block naming [hostId] as parent.
  Future<List<Report>> collect(String hostId, String? machine, DateTime now, int nextSeconds) async {
    if (_at == null || now.difference(_at!) >= const Duration(seconds: 55)) {
      _at = now;
      _last = await _scan();
    }
    return [for (final f in _last) f.report(hostId, machine, now, nextSeconds, _reconnects[f.id] ?? 0)];
  }

  /// The host's own declaration of what hangs off it.
  static List<NodeLink> hostLinks(List<Report> things) => [
        for (final t in things)
          for (final l in t.node?.links ?? const <NodeLink>[])
            NodeLink(to: t.id, via: l.via, dir: l.dir == 'out' ? 'in' : l.dir == 'in' ? 'out' : 'both', detail: l.detail),
      ];

  Future<List<_Found>> _scan() async {
    if (!Platform.isLinux) return const [];
    final out = <_Found>[];
    for (final u in _usb()) {
      final ups = u.ifClasses.contains(0x03) && upsVendors.containsKey(u.vid);
      final audio = u.ifClasses.contains(0x01) && !u.ifClasses.contains(0x0e);
      if (!ups && !audio) continue;
      final id = u.thingId;
      final prev = _devnum[id];
      if (prev != null && prev != u.devnum) _reconnects[id] = (_reconnects[id] ?? 0) + 1;
      _devnum[id] = u.devnum;
      if (ups) {
        out.add(_Found(id, 'ups', u, [..._usbReadings(u, id), ...await _ups(u)]));
      } else {
        out.add(_Found(id, 'audio-interface', u, [..._usbReadings(u, id), ...await _alsa(u)]));
      }
    }
    return out;
  }

  // ---- USB (sysfs) ----------------------------------------------------------

  static String? _read(String p) {
    try {
      final s = File(p).readAsStringSync().trim();
      return s.isEmpty ? null : s;
    } catch (_) {
      return null;
    }
  }

  static List<_Usb> _usb() {
    const base = '/sys/bus/usb/devices';
    final out = <_Usb>[];
    try {
      final names = [for (final e in Directory(base).listSync()) e.uri.pathSegments.where((x) => x.isNotEmpty).last];
      for (final n in names) {
        if (n.contains(':') || n.startsWith('usb')) continue;
        final vid = _read('$base/$n/idVendor'), pid = _read('$base/$n/idProduct');
        if (vid == null || pid == null) continue;
        final ifs = <int>{};
        for (final i in names.where((x) => x.startsWith('$n:'))) {
          final c = int.tryParse(_read('$base/$i/bInterfaceClass') ?? '', radix: 16);
          if (c != null) ifs.add(c);
        }
        out.add(_Usb(
          path: n,
          vid: vid,
          pid: pid,
          maker: _read('$base/$n/manufacturer'),
          product: _read('$base/$n/product'),
          serial: _read('$base/$n/serial'),
          speedMbps: double.tryParse(_read('$base/$n/speed') ?? ''),
          version: _read('$base/$n/version'),
          maxPowerMa: int.tryParse((_read('$base/$n/bMaxPower') ?? '').replaceAll('mA', '')),
          devnum: _read('$base/$n/devnum') ?? '',
          connectedMs: int.tryParse(_read('$base/$n/power/connected_duration') ?? ''),
          ifClasses: ifs,
        ));
      }
    } catch (_) {}
    return out;
  }

  List<Reading> _usbReadings(_Usb u, String id) => [
        Reading(key: 'usb.speed', label: 'Link speed', value: u.speedMbps, unit: 'Mbit/s', group: 'USB', src: u.speedMbps == null ? 'unknown' : 'measured',
            note: u.speedMbps == null ? 'not reported by the system' : null),
        if (u.version != null) Reading(key: 'usb.version', label: 'USB version', value: u.version, group: 'USB', src: 'declared'),
        Reading(key: 'usb.port', label: 'Port', value: 'bus ${u.path.split('-').first}, port ${u.path.split('-').skip(1).join('-')}', group: 'USB', src: 'measured'),
        Reading(key: 'usb.maxPower', label: 'Power it may draw', value: u.maxPowerMa, unit: 'mA', group: 'USB', src: u.maxPowerMa == null ? 'unknown' : 'declared',
            note: u.maxPowerMa == null ? 'not reported' : 'the device\'s own rating, not a measurement'),
        Reading(key: 'usb.current', label: 'Power drawn now', value: null, group: 'USB', src: 'unknown', note: 'USB ports do not measure current'),
        if (u.connectedMs != null)
          Reading(key: 'usb.connectedMin', label: 'Connected for', value: (u.connectedMs! / 60000).round(), unit: 'min', group: 'USB', src: 'measured'),
        Reading(key: 'usb.reconnects', label: 'Reconnects', value: _reconnects[id] ?? 0, group: 'USB', src: 'measured', note: 'since the collector started',
            level: (_reconnects[id] ?? 0) > 0 ? 'warn' : null),
        Reading(key: 'usb.errors', label: 'Transfer errors', value: null, group: 'USB', src: 'unknown', note: 'Linux keeps no per-device USB error count'),
        if (u.serial != null) Reading(key: 'usb.serial', label: 'Serial number', value: u.serial, group: 'USB', src: 'declared'),
        Reading(key: 'usb.id', label: 'USB id', value: '${u.vid}:${u.pid}', group: 'USB', src: 'declared'),
      ];

  // ---- UPS ------------------------------------------------------------------

  Future<List<Reading>> _ups(_Usb u) async {
    // Directly over USB first; NUT or upower only if they happen to run.
    var why = '';
    final hid = HidUps.read(u.path, (w) => why = w);
    if (hid != null) return hid;
    final nut = await _nut();
    if (nut != null) return _upsFromNut(nut);
    final up = await _upower();
    if (up != null) return _upsFromUpower(up);
    return [
      for (final k in const [('ups.status', 'Running on'), ('ups.charge', 'Battery charge'), ('ups.runtime', 'Runtime left'), ('ups.load', 'Load')])
        Reading(key: k.$1, label: k.$2, value: null, group: 'Power', src: 'unknown', note: why),
    ];
  }

  /// Asks a NUT server on this machine (port 3493) for every variable of
  /// its first UPS. Plain text protocol, no NUT tools needed here.
  static Future<Map<String, String>?> _nut() async {
    Socket? s;
    try {
      s = await Socket.connect('127.0.0.1', 3493, timeout: const Duration(milliseconds: 800));
      final lines = StreamIterator(s.cast<List<int>>().transform(utf8.decoder).transform(const LineSplitter()));
      Future<List<String>> ask(String cmd, String end) async {
        s!.write('$cmd\n');
        final got = <String>[];
        while (await lines.moveNext().timeout(const Duration(seconds: 2))) {
          final l = lines.current;
          if (l.startsWith('ERR')) return got;
          if (l.startsWith(end)) break;
          got.add(l);
        }
        return got;
      }

      final ups = (await ask('LIST UPS', 'END LIST UPS')).where((l) => l.startsWith('UPS ')).map((l) => l.split(' ')[1]).toList();
      if (ups.isEmpty) return null;
      final vars = <String, String>{'_ups': ups.first};
      for (final l in await ask('LIST VAR ${ups.first}', 'END LIST VAR')) {
        final m = RegExp(r'^VAR \S+ (\S+) "(.*)"$').firstMatch(l);
        if (m != null) vars[m.group(1)!] = m.group(2)!;
      }
      return vars;
    } catch (_) {
      return null;
    } finally {
      s?.destroy();
    }
  }

  static List<Reading> _upsFromNut(Map<String, String> v) {
    double? n(String k) => double.tryParse(v[k] ?? '');
    final status = v['ups.status'] ?? '';
    final onBattery = status.contains('OB'), low = status.contains('LB');
    final charge = n('battery.charge');
    return [
      Reading(key: 'ups.status', label: 'Running on', value: low ? 'battery, low' : onBattery ? 'battery' : status.contains('OL') ? 'mains' : status, group: 'Power',
          src: 'measured', level: low ? 'fault' : onBattery ? 'warn' : null),
      _num('ups.charge', 'Battery charge', charge, '%', 'measured', level: charge == null ? null : charge < 20 ? 'fault' : charge < 50 && onBattery ? 'warn' : null),
      _num('ups.runtime', 'Runtime left', n('battery.runtime') == null ? null : n('battery.runtime')! / 60, 'min', 'estimated', note: 'the UPS\'s own estimate at the current load'),
      _num('ups.load', 'Load', n('ups.load'), '%', 'measured'),
      if (v['ups.mfr'] != null || v['device.mfr'] != null) Reading(key: 'ups.maker', label: 'Maker', value: v['ups.mfr'] ?? v['device.mfr'], group: 'Power', src: 'declared'),
      if (v['ups.model'] != null || v['device.model'] != null)
        Reading(key: 'ups.model', label: 'Model', value: v['ups.model'] ?? v['device.model'], group: 'Power', src: 'declared'),
      Reading(key: 'ups.source', label: 'Read through', value: 'NUT (${v['driver.name'] ?? 'usbhid-ups'}, "${v['_ups']}")', group: 'Power', src: 'measured'),
    ];
  }

  static Reading _num(String key, String label, double? v, String unit, String src, {String? note, String? level}) => Reading(
      key: key,
      label: label,
      value: v == null ? null : (v * 10).round() / 10,
      unit: unit,
      group: 'Power',
      src: v == null ? 'unknown' : src,
      note: v == null ? 'this UPS does not report it' : note,
      level: level);

  static Future<Map<String, String>?> _upower() async {
    try {
      final e = await Process.run('upower', ['-e']).timeout(const Duration(seconds: 3));
      final path = (e.stdout as String).split('\n').map((l) => l.trim()).firstWhere((l) => l.contains('/ups_'), orElse: () => '');
      if (path.isEmpty) return null;
      final i = await Process.run('upower', ['-i', path]).timeout(const Duration(seconds: 3));
      final out = <String, String>{};
      for (final l in (i.stdout as String).split('\n')) {
        final k = l.indexOf(':');
        if (k > 0) out[l.substring(0, k).trim()] = l.substring(k + 1).trim();
      }
      return out;
    } catch (_) {
      return null;
    }
  }

  static List<Reading> _upsFromUpower(Map<String, String> v) {
    final pct = double.tryParse((v['percentage'] ?? '').replaceAll('%', '').trim());
    final state = v['state'] ?? '';
    final onBattery = state == 'discharging';
    double? hours(String? s) {
      if (s == null) return null;
      final m = RegExp(r'([\d.,]+)\s*(hours?|minutes?)').firstMatch(s);
      if (m == null) return null;
      final x = double.tryParse(m.group(1)!.replaceAll(',', '.'));
      return x == null ? null : (m.group(2)!.startsWith('hour') ? x * 60 : x);
    }

    const none = 'upower does not read it; NUT (usbhid-ups) does';
    return [
      Reading(key: 'ups.status', label: 'Running on', value: onBattery ? 'battery' : 'mains', group: 'Power', src: 'measured', level: onBattery ? 'warn' : null),
      _num('ups.charge', 'Battery charge', pct, '%', 'measured', level: pct != null && pct < 20 ? 'fault' : null),
      _num('ups.runtime', 'Runtime left', hours(v['time to empty']), 'min', 'estimated', note: 'the UPS\'s own estimate'),
      Reading(key: 'ups.load', label: 'Load', value: null, group: 'Power', src: 'unknown', note: none),
      if (v['vendor'] != null) Reading(key: 'ups.maker', label: 'Maker', value: v['vendor'], group: 'Power', src: 'declared'),
      if (v['model'] != null) Reading(key: 'ups.model', label: 'Model', value: v['model'], group: 'Power', src: 'declared'),
      Reading(key: 'ups.source', label: 'Read through', value: 'upower', group: 'Power', src: 'measured'),
    ];
  }

  // ---- Audio (ALSA) ---------------------------------------------------------

  static Future<List<Reading>> _alsa(_Usb u) async {
    // The ALSA card whose usbid matches this USB device.
    String? card;
    try {
      for (final e in Directory(_asound).listSync()) {
        final n = e.uri.pathSegments.where((x) => x.isNotEmpty).last;
        if (!RegExp(r'^card\d+$').hasMatch(n)) continue;
        if (_read('$_asound/$n/usbid') == '${u.vid}:${u.pid}') card = n;
      }
    } catch (_) {}
    if (card == null) {
      return [Reading(key: 'audio.card', label: 'Sound card', value: null, group: 'Audio', src: 'unknown', note: 'no ALSA card for it on this system (in Docker, mount /proc/asound at /host/asound)')];
    }
    final out = <Reading>[
      Reading(key: 'audio.card', label: 'Sound card', value: '${_read('$_asound/$card/id') ?? card} ($card)', group: 'Audio', src: 'measured'),
    ];
    // stream0: what the USB audio driver negotiated, per direction.
    final stream = _read('$_asound/$card/stream0') ?? '';
    for (final dir in const ['Playback', 'Capture']) {
      final i = stream.indexOf('$dir:');
      if (i < 0) continue;
      final next = stream.indexOf(RegExp(r'^(Playback|Capture):', multiLine: true), i + 1);
      final sec = stream.substring(i, next < 0 ? stream.length : next);
      final status = RegExp(r'Status:\s*(\w+)').firstMatch(sec)?.group(1);
      final freq = RegExp(r'Momentary freq = (\d+) Hz').firstMatch(sec)?.group(1);
      final ch = RegExp(r'Channels:\s*(\d+)').firstMatch(sec)?.group(1);
      final fmt = RegExp(r'Format:\s*(\S+)').firstMatch(sec)?.group(1);
      final k = dir.toLowerCase();
      out.addAll([
        Reading(key: 'audio.$k.state', label: '$dir', value: status == 'Running' ? 'running' : status == 'Stop' ? 'stopped' : status, group: 'Audio', src: 'measured'),
        if (freq != null) Reading(key: 'audio.$k.rate', label: '$dir sample rate', value: int.parse(freq), unit: 'Hz', group: 'Audio', src: 'measured'),
        if (ch != null) Reading(key: 'audio.$k.channels', label: '$dir channels', value: int.parse(ch), group: 'Audio', src: 'declared'),
        if (fmt != null) Reading(key: 'audio.$k.format', label: '$dir format', value: fmt, group: 'Audio', src: 'declared'),
      ]);
      // hw_params of an open stream: the rate and buffer actually in use.
      final hw = _read('$_asound/$card/pcm0${k[0]}/sub0/hw_params');
      if (hw != null && hw != 'closed') {
        final rate = RegExp(r'rate:\s*(\d+)').firstMatch(hw)?.group(1);
        final buf = RegExp(r'buffer_size:\s*(\d+)').firstMatch(hw)?.group(1);
        final per = RegExp(r'period_size:\s*(\d+)').firstMatch(hw)?.group(1);
        if (rate != null && freq == null) out.add(Reading(key: 'audio.$k.rate', label: '$dir sample rate', value: int.parse(rate), unit: 'Hz', group: 'Audio', src: 'measured'));
        if (buf != null) out.add(Reading(key: 'audio.$k.buffer', label: '$dir buffer', value: int.parse(buf), unit: 'frames', group: 'Audio', src: 'measured'));
        if (per != null) out.add(Reading(key: 'audio.$k.period', label: '$dir period', value: int.parse(per), unit: 'frames', group: 'Audio', src: 'measured'));
      }
    }
    out.add(Reading(key: 'audio.xruns', label: 'Dropouts (xruns)', value: null, group: 'Audio', src: 'unknown',
        note: 'ALSA counts them only with xrun_debug switched on, and the sound server (PipeWire) keeps its own'));
    out.addAll(await _mixer(card.substring(4)));
    return out;
  }

  /// Simple mixer controls with a level, through amixer when it is installed.
  static Future<List<Reading>> _mixer(String index) async {
    try {
      final r = await Process.run('amixer', ['-c', index, 'scontents']).timeout(const Duration(seconds: 2));
      if (r.exitCode != 0) return const [];
      final out = <Reading>[];
      String? name;
      for (final l in (r.stdout as String).split('\n')) {
        final m = RegExp(r"^Simple mixer control '(.+)',\d+").firstMatch(l);
        if (m != null) {
          name = m.group(1);
          continue;
        }
        final p = RegExp(r'\[(\d+)%\]').firstMatch(l);
        if (name != null && p != null && out.length < 12) {
          out.add(Reading(key: 'mixer.${name.toLowerCase().replaceAll(RegExp(r'[^a-z0-9]+'), '-')}', label: name, value: int.parse(p.group(1)!), unit: '%', group: 'Mixer', src: 'measured'));
          name = null;
        }
      }
      return out;
    } catch (_) {
      return const [];
    }
  }
}

class _Usb {
  _Usb({
    required this.path,
    required this.vid,
    required this.pid,
    this.maker,
    this.product,
    this.serial,
    this.speedMbps,
    this.version,
    this.maxPowerMa,
    required this.devnum,
    this.connectedMs,
    required this.ifClasses,
  });
  final String path, vid, pid, devnum;
  final String? maker, product, serial, version;
  final double? speedMbps;
  final int? maxPowerMa, connectedMs;
  final Set<int> ifClasses;

  /// Stable across reboots and across the systems of a dual-boot machine:
  /// vendor, product and serial (or the port when there is no serial).
  String get thingId => 'usb-$vid-$pid-${(serial ?? 'port$path').toLowerCase().replaceAll(RegExp(r'[^a-z0-9]+'), '')}';

  String get name {
    final m = (maker ?? Things.upsVendors[vid] ?? '').trim(), p = (product ?? '').trim();
    if (p.isEmpty) return m.isEmpty ? 'USB device $vid:$pid' : m;
    return m.isEmpty || p.toLowerCase().startsWith(m.toLowerCase()) ? p : '$m $p';
  }
}

class _Found {
  _Found(this.id, this.type, this.usb, this.readings);
  final String id, type;
  final _Usb usb;
  final List<Reading> readings;

  Report report(String host, String? machine, DateTime now, int next, int reconnects) {
    final speed = usb.speedMbps == null ? '' : ', ${usb.speedMbps! >= 1000 ? '${(usb.speedMbps! / 1000).toStringAsFixed(0)} Gbit/s' : '${usb.speedMbps!.round()} Mbit/s'}';
    return Report(
      id: id,
      name: usb.name,
      kind: 'thing',
      time: now,
      machine: machine,
      nextSeconds: next,
      connected: true,
      readings: readings,
      node: NodeDecl(id: id, type: type, parent: host, links: [
        NodeLink(to: host, via: 'usb', dir: type == 'ups' ? 'out' : 'both', detail: 'USB ${usb.version ?? ''}$speed'.trim()),
        if (type == 'ups') NodeLink(to: host, via: 'power', dir: 'out', detail: 'mains or battery power'),
      ]),
    );
  }
}
