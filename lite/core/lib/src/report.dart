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
        id: id,
        name: name is String && name.isNotEmpty ? name : id,
        kind: kind is String ? kind : 'unknown',
        time: _sec(t),
        os: _str(json['os']),
        machine: _str(json['machine']),
        app: _str(json['app']),
        seq: _int(json['seq']) ?? 0,
        nextSeconds: (_int(json['next']) ?? 60).clamp(10, 3600),
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
      };

  /// The compact line appended to the daily history file.
  Map<String, Object?> historyLine() => {
        't': time.millisecondsSinceEpoch ~/ 1000,
        if (battery?.pct != null) 'b': battery!.pct,
        if (battery?.charging != null) 'c': battery!.charging! ? 1 : 0,
        if (cpuPct != null) 'cpu': _round1(cpuPct!),
        if (memTotal != null && memUsed != null) 'mem': _round1(100 * memUsed! / memTotal!),
        if (diskTotal != null && diskUsed != null) 'disk': _round1(100 * diskUsed! / diskTotal!),
        if (netRxBps != null) 'rx': netRxBps!.round(),
        if (netTxBps != null) 'tx': netTxBps!.round(),
        'p': {for (final e in peers.entries) e.key: e.value.reachable ? 1 : 0},
        for (final r in readings)
          if (r.value is num) r.key: r.value,
      };
}

/// One named reading, e.g. {key: "temp.cpu", label: "CPU temperature",
/// value: 54.2, unit: "°C", group: "Sensors"}. Value may be a number, text
/// or true/false.
class Reading {
  Reading({required this.key, required this.value, this.label, this.unit, this.group});
  final String key;
  final Object value;
  final String? label, unit, group;

  static Reading? tryParse(Object? j) {
    if (j is! Map) return null;
    final k = j['key'], v = j['value'];
    if (k is! String || k.isEmpty || !(v is num || v is String || v is bool)) return null;
    return Reading(key: k, value: v as Object, label: _str(j['label']), unit: _str(j['unit']), group: _str(j['group']));
  }

  Map<String, Object?> toJson() => {
        'key': key,
        'value': value,
        if (label != null) 'label': label,
        if (unit != null) 'unit': unit,
        if (group != null) 'group': group,
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
  PeerView({this.tcp, this.pingMs, this.seenAge});

  /// TCP connect to the peer's Syncthing port over Tailscale:
  /// open (Syncthing listening), refused (device up, Syncthing not listening),
  /// timeout (no route), noip (no Tailscale address known), error.
  final String? tcp;

  /// ICMP ping round trip over Tailscale, null when it failed or wasn't tried.
  final int? pingMs;

  /// Age in seconds of the peer's now.json as this device last saw it, i.e.
  /// how fresh Syncthing keeps that peer here. Null if never seen.
  final int? seenAge;

  /// Tailscale path to the device works.
  bool get reachable => tcp == 'open' || tcp == 'refused' || pingMs != null;

  /// The device answered nothing at all.
  bool get unreachable => !reachable && (tcp == 'timeout' || tcp == 'error');

  static PeerView? tryParse(Object? j) {
    if (j is! Map) return null;
    return PeerView(tcp: _str(j['tcp']), pingMs: _int(j['ping']), seenAge: _int(j['seen']));
  }

  Map<String, Object?> toJson() => {
        if (tcp != null) 'tcp': tcp,
        if (pingMs != null) 'ping': pingMs,
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
