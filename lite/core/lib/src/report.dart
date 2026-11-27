import 'control.dart';
import 'topology.dart';

/// One device's latest report, as written to devices/<id>/now.json.
///
/// Every field except id/name/kind/t is optional: a reading the device cannot
/// take is simply absent, and the UI says "not available" instead of guessing.
class Report {
  Report({
    required this.id,
    required this.name,
    required this.kind,
    required this.time,
    this.os,
    this.machine,
    this.app,
    this.seq = 0,
    this.nextSeconds = 60,
    this.watchedBy = const [],
    this.bootTime,
    this.battery,
    this.cpuPct,
    this.cpuNote,
    this.memUsed,
    this.memTotal,
    this.diskUsed,
    this.diskTotal,
    this.netRxBps,
    this.netTxBps,
    this.netType,
    this.tailscaleIp,
    this.syncthingLocal,
    this.screenOn,
    Map<String, PeerView>? peers,
    this.self,
    List<Reading>? readings,
    this.raw = const {},
    this.node,
    this.connected,
    this.controls = const [],
  })  : peers = peers ?? {},
        readings = readings ?? [];

  /// Stable random id for this install (one per OS on a dual-boot machine).
  final String id;
  final String name;

  /// windows | linux | android
  final String kind;

  /// When the device wrote this report (UTC).
  final DateTime time;
  final String? os;

  /// Same value for every OS installed on one physical machine (dual boot).
  final String? machine;
  final String? app;
  final int seq;

  /// How many seconds until the device plans to write again.
  final int nextSeconds;

  /// Devices whose app was open when this was written. While the list is not
  /// empty the device collects at the fast "live" rate.
  final List<String> watchedBy;

  /// Collecting at the live rate right now.
  bool get live => nextSeconds <= 20;
  final DateTime? bootTime;
  final Battery? battery;
  final double? cpuPct;
  final String? cpuNote;
  final int? memUsed, memTotal, diskUsed, diskTotal;
  final double? netRxBps, netTxBps;
  final String? netType;
  final String? tailscaleIp;

  /// Whether Syncthing is listening on this device (checked locally).
  final bool? syncthingLocal;
  final bool? screenOn;

  /// What this device sees of each other device, keyed by device id.
  final Map<String, PeerView> peers;
  final SelfCost? self;

  /// Open-ended extra readings (name, value, unit, group). New kinds of data
  /// (more sensors, services, IoT things) go here without changing the format;
  /// the app shows any it finds, grouped.
  final List<Reading> readings;

  /// The whole file as read, including fields this version doesn't know.
  /// Nothing is thrown away, so newer writers never break older readers and
  /// any field can be pulled out by path with [field].
  final Map<String, Object?> raw;

  /// Where this sits in the hierarchy and what it is connected to (FORMAT.md
  /// "node"). Null in files written before 2.5.
  final NodeDecl? node;

  /// Things only: false when the host no longer sees it (unplugged).
  final bool? connected;

  /// Settings this device (or thing) lets other devices change (control.dart).
  final List<Control> controls;

  bool get isThing => kind == 'thing';

  /// Format version the writer used. Readers accept any version and use the
  /// fields they recognise.
  int get formatVersion => (raw['v'] as num?)?.toInt() ?? 1;

  /// Any value by dotted path, e.g. "battery.pct", "peers.<id>.tcp",
  /// "net.type", or "readings.<key>" for an extra reading. Null when absent.
  Object? field(String path) => fieldAt(raw, path);

  /// [field] as a number, or null.
  double? number(String path) {
    final v = field(path);
    return v is num ? v.toDouble() : null;
  }

  /// Every leaf value in the file as path -> value, for a generic
  /// "all fields" view and for picking any field for status or history.
  Map<String, Object?> flatten() => flattenJson(raw);

  static DateTime _sec(num s) => DateTime.fromMillisecondsSinceEpoch((s * 1000).round(), isUtc: true);

  static Report? tryParse(Object? json) {
    try {
      if (json is! Map) return null;
      final id = json['id'], name = json['name'], kind = json['kind'], t = json['t'];
      if (id is! String || id.isEmpty || t is! num) return null;
      final peersJson = json['peers'];
      final peers = <String, PeerView>{};
      if (peersJson is Map) {
        peersJson.forEach((k, v) {
          final p = PeerView.tryParse(v);
          if (k is String && p != null) peers[k] = p;
        });
      }
      final mem = json['mem'], disk = json['disk'], net = json['net'], ts = json['ts'];
      return Report(
        raw: Map<String, Object?>.from(json),
        id: id,
        name: name is String && name.isNotEmpty ? name : id,
        kind: kind is String ? kind : 'unknown',
        time: _sec(t),
        os: _str(json['os']),
        machine: _str(json['machine']),
        app: _str(json['app']),
        seq: _int(json['seq']) ?? 0,
        nextSeconds: (_int(json['next']) ?? 60).clamp(10, 3600),
        watchedBy: json['watch'] is List ? [for (final w in json['watch'] as List) if (w is String) w] : const [],
        bootTime: json['boot'] is num ? _sec(json['boot'] as num) : null,
        battery: Battery.tryParse(json['battery']),
        cpuPct: json['cpu'] is Map ? _dbl((json['cpu'] as Map)['pct']) : null,
        cpuNote: _str(json['cpuNote']),
        memUsed: mem is Map ? _int(mem['used']) : null,
        memTotal: mem is Map ? _int(mem['total']) : null,
        diskUsed: disk is Map ? _int(disk['used']) : null,
        diskTotal: disk is Map ? _int(disk['total']) : null,
        netRxBps: net is Map ? _dbl(net['rxBps']) : null,
        netTxBps: net is Map ? _dbl(net['txBps']) : null,
        netType: net is Map ? _str(net['type']) : null,
        tailscaleIp: ts is Map ? _str(ts['ip']) : null,
        syncthingLocal: json['st'] is Map ? (json['st'] as Map)['up'] as bool? : null,
        screenOn: json['screen'] is String ? json['screen'] == 'on' : null,
        peers: peers,
        self: SelfCost.tryParse(json['self']),
        node: NodeDecl.tryParse(json['node']),
        connected: json['connected'] is bool ? json['connected'] as bool : null,
        controls: json['controls'] is List ? (json['controls'] as List).map(Control.tryParse).whereType<Control>().toList() : const [],
        readings: json['readings'] is List
            ? (json['readings'] as List).map(Reading.tryParse).whereType<Reading>().toList()
            : null,
      );
    } catch (_) {
      return null;
    }
  }

  Map<String, Object?> toJson() => {
        'v': 1,
        'id': id,
        'name': name,
        'kind': kind,
        't': time.millisecondsSinceEpoch ~/ 1000,
        'seq': seq,
        'next': nextSeconds,
        if (watchedBy.isNotEmpty) 'watch': watchedBy,
        if (os != null) 'os': os,
        if (machine != null) 'machine': machine,
        if (app != null) 'app': app,
        if (bootTime != null) 'boot': bootTime!.millisecondsSinceEpoch ~/ 1000,
        if (battery != null) 'battery': battery!.toJson(),
        if (cpuPct != null) 'cpu': {'pct': _round1(cpuPct!)},
        if (cpuNote != null) 'cpuNote': cpuNote,
        if (memTotal != null) 'mem': {'used': memUsed, 'total': memTotal},
        if (diskTotal != null) 'disk': {'used': diskUsed, 'total': diskTotal},
        if (netRxBps != null || netType != null)
          'net': {
            if (netRxBps != null) 'rxBps': netRxBps!.round(),
            if (netTxBps != null) 'txBps': netTxBps!.round(),
            if (netType != null) 'type': netType,
          },
        'ts': {'ip': tailscaleIp},
        if (syncthingLocal != null) 'st': {'up': syncthingLocal},
        if (screenOn != null) 'screen': screenOn! ? 'on' : 'off',
        'peers': {for (final e in peers.entries) e.key: e.value.toJson()},
        if (self != null) 'self': self!.toJson(),
        if (readings.isNotEmpty) 'readings': [for (final r in readings) r.toJson()],
        if (node != null) 'node': node!.toJson(),
        if (connected != null) 'connected': connected,
        if (controls.isNotEmpty) 'controls': [for (final c in controls) c.toJson()],
      };

  /// The compact line appended to the daily history file.
  Map<String, Object?> historyLine() => {
        't': time.millisecondsSinceEpoch ~/ 1000,
        if (battery?.pct != null) 'b': battery!.pct,
        if (battery?.charging != null) 'c': battery!.charging! ? 1 : 0,
        if (screenOn != null) 's': screenOn! ? 1 : 0,
        if (cpuPct != null) 'cpu': _round1(cpuPct!),
        if (memTotal != null && memUsed != null) 'mem': _round1(100 * memUsed! / memTotal!),
        if (diskTotal != null && diskUsed != null) 'disk': _round1(100 * diskUsed! / diskTotal!),
        if (netRxBps != null) 'rx': netRxBps!.round(),
        if (netTxBps != null) 'tx': netTxBps!.round(),
        'p': {for (final e in peers.entries) e.key: e.value.reachable ? 1 : 0},
        for (final r in readings)
          if (r.value is num) r.key: r.value,
        // A UPS's mains/battery state as a number, so history and patterns
        // can count power cuts.
        for (final r in readings)
          if (r.key == 'ups.status' && r.value is String) 'ups.onBattery': (r.value as String).startsWith('battery') ? 1 : 0,
      };
}

/// One named reading, e.g. {key: "temp.cpu", label: "CPU temperature",
/// value: 54.2, unit: "°C", group: "Sensors"}. Value may be a number, text
/// or true/false.
class Reading {
  Reading({required this.key, required this.value, this.label, this.unit, this.group, this.level, this.src, this.note, this.from});
  final String key;

  /// A number, text or yes/no; null only when [src] is "unknown".
  final Object? value;
  final String? label, unit, group;

  /// Where the value comes from: "measured" (read from the hardware now),
  /// "declared" (what the hardware says about itself, e.g. its rated
  /// power), "estimated" (worked out, e.g. runtime left) or "unknown" (not
  /// available, with [note] saying why). Missing means measured.
  final String? src;
  final String? note;

  /// Which path the value came through, where a part can be read more than
  /// one way (a UPS today): "upower" (UPower on this machine),
  /// "upower-export" (the host's UPower, through its exported file), "hid"
  /// (the device directly over USB HID), "nut" or "sysfs". Missing means
  /// the device's usual path.
  final String? from;

  /// Optional judgement by the device itself: "warn" or "fault". The app
  /// marks the reading's part with it.
  final String? level;

  /// The same reading, marked as having come through [path].
  Reading withFrom(String path, {String? note}) =>
      Reading(key: key, value: value, label: label, unit: unit, group: group, level: level, src: src, note: note ?? this.note, from: path);

  static Reading? tryParse(Object? j) {
    if (j is! Map) return null;
    final k = j['key'], v = j['value'], src = _str(j['src']);
    if (k is! String || k.isEmpty) return null;
    if (!(v is num || v is String || v is bool || (v == null && src == 'unknown'))) return null;
    return Reading(
        key: k,
        value: v,
        label: _str(j['label']),
        unit: _str(j['unit']),
        group: _str(j['group']),
        level: _str(j['level']),
        src: src,
        note: _str(j['note']),
        from: _str(j['from']));
  }

  Map<String, Object?> toJson() => {
        'key': key,
        'value': value,
        if (label != null) 'label': label,
        if (unit != null) 'unit': unit,
        if (group != null) 'group': group,
        if (level != null) 'level': level,
        if (src != null) 'src': src,
        if (note != null) 'note': note,
        if (from != null) 'from': from,
      };
}

class Battery {
  Battery({this.pct, this.charging, this.plugged, this.tempC});
  final double? pct;
  final bool? charging, plugged;
  final double? tempC;

  static Battery? tryParse(Object? j) {
    if (j is! Map) return null;
    return Battery(
      pct: _dbl(j['pct']),
      charging: j['charging'] as bool?,
      plugged: j['plugged'] as bool?,
      tempC: _dbl(j['tempC']),
    );
  }

  Map<String, Object?> toJson() => {
        if (pct != null) 'pct': _round1(pct!),
        if (charging != null) 'charging': charging,
        if (plugged != null) 'plugged': plugged,
        if (tempC != null) 'tempC': _round1(tempC!),
      };
}

/// One device's view of another device.
class PeerView {
  PeerView({this.tcp, this.pingMs, this.tcpMs, this.seenAge});

  /// TCP connect to the peer's Syncthing port over Tailscale:
  /// open (Syncthing listening), refused (device up, Syncthing not listening),
  /// timeout (no route), noip (no Tailscale address known), error.
  final String? tcp;

  /// ICMP ping round trip over Tailscale, null when it failed or wasn't tried.
  final int? pingMs;

  /// TCP handshake time to the peer's Syncthing port (open or refused), ms.
  /// Computers only; works where ICMP is not allowed (Docker).
  final int? tcpMs;

  /// Age in seconds of the peer's now.json as this device last saw it, i.e.
  /// how fresh Syncthing keeps that peer here. Null if never seen.
  final int? seenAge;

  /// Tailscale path to the device works.
  bool get reachable => tcp == 'open' || tcp == 'refused' || pingMs != null;

  /// The device answered nothing at all.
  bool get unreachable => !reachable && (tcp == 'timeout' || tcp == 'error');

  static PeerView? tryParse(Object? j) {
    if (j is! Map) return null;
    return PeerView(tcp: _str(j['tcp']), pingMs: _int(j['ping']), tcpMs: _int(j['tcpMs']), seenAge: _int(j['seen']));
  }

  Map<String, Object?> toJson() => {
        if (tcp != null) 'tcp': tcp,
        if (pingMs != null) 'ping': pingMs,
        if (tcpMs != null) 'tcpMs': tcpMs,
        if (seenAge != null) 'seen': seenAge,
      };
}

/// What the collector cost the device on its last run (self-measured budget).
class SelfCost {
  SelfCost({this.rssMb, this.runMs, this.cpuMs, this.over = const []});
  final double? rssMb;
  final int? runMs, cpuMs;

  /// Budget lines that were exceeded, e.g. "rss 41MB > 30MB".
  final List<String> over;

  static SelfCost? tryParse(Object? j) {
    if (j is! Map) return null;
    final o = j['over'];
    return SelfCost(
      rssMb: _dbl(j['rssMb']),
      runMs: _int(j['runMs']),
      cpuMs: _int(j['cpuMs']),
      over: o is List ? o.whereType<String>().toList() : const [],
    );
  }

  Map<String, Object?> toJson() => {
        if (rssMb != null) 'rssMb': _round1(rssMb!),
        if (runMs != null) 'runMs': runMs,
        if (cpuMs != null) 'cpuMs': cpuMs,
        if (over.isNotEmpty) 'over': over,
      };
}

String? _str(Object? v) => v is String && v.isNotEmpty ? v : null;
int? _int(Object? v) => v is num ? v.round() : null;
double? _dbl(Object? v) => v is num ? v.toDouble() : null;
double _round1(double v) => (v * 10).roundToDouble() / 10;

/// Looks up a dotted path in decoded JSON. Lists of objects with a "key"
/// field (like readings) are addressed by that key: "readings.temp.cpu"
/// matches the reading whose key is "temp.cpu".
Object? fieldAt(Object? json, String path) {
  if (path.isEmpty) return json;
  if (json is Map) {
    if (json.containsKey(path)) return json[path];
    // Longest matching prefix first, so keys containing dots still work.
    final parts = path.split('.');
    for (var i = parts.length - 1; i >= 1; i--) {
      final head = parts.take(i).join('.');
      if (json.containsKey(head)) {
        final r = fieldAt(json[head], parts.skip(i).join('.'));
        if (r != null) return r;
      }
    }
    return null;
  }
  if (json is List) {
    for (final e in json) {
      if (e is Map && e['key'] == path) return e['value'];
    }
    final dot = path.indexOf('.');
    final idx = int.tryParse(dot < 0 ? path : path.substring(0, dot));
    if (idx != null && idx >= 0 && idx < json.length) return dot < 0 ? json[idx] : fieldAt(json[idx], path.substring(dot + 1));
    for (final e in json) {
      if (e is Map && e['key'] is String && path.startsWith('${e['key']}.')) {
        return fieldAt(e, path.substring((e['key'] as String).length + 1));
      }
    }
  }
  return null;
}

Map<String, Object?> flattenJson(Object? json, [String prefix = '']) {
  final out = <String, Object?>{};
  void walk(Object? v, String p) {
    if (v is Map) {
      v.forEach((k, x) => walk(x, p.isEmpty ? '$k' : '$p.$k'));
    } else if (v is List && v.every((e) => e is Map && e['key'] is String && e.containsKey('value'))) {
      for (final e in v) {
        out['$p.${(e as Map)['key']}'] = e['value'];
      }
    } else if (v is List) {
      for (var i = 0; i < v.length; i++) {
        walk(v[i], '$p.$i');
      }
    } else {
      out[p] = v;
    }
  }

  walk(json, prefix);
  return out;
}

/// One series out of history lines by path, e.g. "cpu" or "p.<peer id>".
List<double?> seriesOf(List<Map<String, Object?>> lines, String path) =>
    [for (final l in lines) switch (fieldAt(l, path)) { num n => n.toDouble(), _ => null }];
