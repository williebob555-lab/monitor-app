import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';

import 'theme.dart';

/// One part of a device that the Dive can open: battery, CPU, memory, disk,
/// network, links (Tailscale and Syncthing), and one per group of extra
/// readings. Built from the report alone, so a new kind of reading shows up
/// as a part without an app change.
class Part {
  Part({
    required this.id,
    required this.title,
    required this.icon,
    required this.value,
    this.sub,
    this.level = Level.ok,
    this.series = const [],
    this.facts = const [],
    this.networkChecks = const [],
  });

  final String id;
  final String title;
  final IconData icon;

  /// The headline value, e.g. "62%" or "1.2 MB/s".
  final String value;
  final String? sub;
  final Level level;

  /// The numbers charted for this part.
  final List<Series> series;

  /// Label/value lines shown when the part is opened.
  final List<(String, String)> facts;
  final List<Reading> networkChecks;
}

/// One charted number: how to read it from a live report and from a
/// history line, and how to print it.
class Series {
  const Series(this.label, this.historyKey, this.live, {this.max, this.unit = '', this.rate = false});
  final String label;
  final String historyKey;
  final double? Function(Report) live;
  final double? max;
  final String unit;
  final bool rate;

  String fmt(double? v) {
    if (v == null) return '-';
    if (rate) return fmtRate(v);
    if (unit == '%') return '${v.round()}%';
    final t = v.abs() >= 100 ? v.round().toString() : v.toStringAsFixed(1);
    return unit.isEmpty ? t : '$t $unit';
  }
}

Level _high(double? v) => v == null ? Level.inactive : v >= 95 ? Level.fault : v >= 85 ? Level.warn : Level.ok;

String _pct(double? v) => v == null ? 'n/a' : '${v.round()}%';

const _networkCheckKeys = ['net.gateway', 'net.dns', 'net.dnsDirect', 'net.internet'];
const _networkDetailKeys = ['net.ifaces', 'net.pihole'];

/// [away]: devices not running right now (the other system of a dual-boot
/// machine, an old copy, one not seen for a while). Not answering is
/// expected of them, so they don't count against this device's links.
List<Part> partsOf(Report r, Map<String, String> names, {Set<String> away = const {}}) {
  final out = <Part>[];
  final readings = {for (final reading in r.readings) reading.key: reading};
  final b = r.battery;
  if (b?.pct != null) {
    final p = b!.pct!;
    out.add(Part(
      id: 'battery',
      title: 'Battery',
      icon: b.charging == true ? Icons.battery_charging_full : Icons.battery_std,
      value: _pct(p),
      sub: b.charging == true ? 'charging' : b.plugged == true ? 'plugged in' : 'on battery',
      level: p < 10 ? Level.fault : p < 20 ? Level.warn : Level.ok,
      series: [Series('Battery', 'b', (x) => x.battery?.pct, max: 100, unit: '%')],
      facts: [
        ('Level', _pct(p)),
        ('Power', b.charging == true ? 'charging' : b.plugged == true ? 'plugged in, not charging' : 'on battery'),
        if (b.tempC != null) ('Temperature', '${b.tempC!.toStringAsFixed(1)} °C'),
      ],
    ));
  }
  // A thing (UPS, audio interface) has none of a computer's own parts.
  if (!r.isThing) {
  out.add(Part(
    id: 'cpu',
    title: 'Processor',
    icon: Icons.memory,
    value: r.cpuPct == null ? 'n/a' : _pct(r.cpuPct),
    sub: r.cpuPct == null ? (r.kind == 'android' ? 'Android hides it' : 'not reported') : 'in use',
    level: r.cpuPct == null ? Level.inactive : _high(r.cpuPct),
    series: [if (r.cpuPct != null) Series('CPU', 'cpu', (x) => x.cpuPct, max: 100, unit: '%')],
    facts: [
      ('In use', r.cpuPct == null ? 'not available' : _pct(r.cpuPct)),
      if (r.cpuNote != null) ('Note', r.cpuNote!),
    ],
  ));
  final mem = pct(r.memUsed, r.memTotal);
  out.add(Part(
    id: 'memory',
    title: 'Memory',
    icon: Icons.developer_board,
    value: _pct(mem),
    sub: '${fmtBytes(r.memUsed)} of ${fmtBytes(r.memTotal)}',
    level: _high(mem),
    series: [Series('Memory', 'mem', (x) => pct(x.memUsed, x.memTotal), max: 100, unit: '%')],
    facts: [('Used', fmtBytes(r.memUsed)), ('Total', fmtBytes(r.memTotal)), ('Free', fmtBytes(_minus(r.memTotal, r.memUsed)))],
  ));
  final disk = pct(r.diskUsed, r.diskTotal);
  out.add(Part(
    id: 'disk',
    title: 'Storage',
    icon: Icons.storage,
    value: _pct(disk),
    sub: '${fmtBytes(_minus(r.diskTotal, r.diskUsed))} free',
    level: _high(disk),
    series: [Series('Storage', 'disk', (x) => pct(x.diskUsed, x.diskTotal), max: 100, unit: '%')],
    facts: [('Used', fmtBytes(r.diskUsed)), ('Free', fmtBytes(_minus(r.diskTotal, r.diskUsed))), ('Total', fmtBytes(r.diskTotal))],
  ));
  final networkChecks = [
    for (final key in _networkCheckKeys)
      if (readings[key] case final reading? when reading.group == 'Network') reading,
  ];
  final networkDetails = r.readings.where((x) => x.group == 'Network' && _networkDetailKeys.contains(x.key)).toList();
  final networkLevel = _readingLevel([...networkChecks, ...networkDetails]);
  out.add(Part(
    id: 'network',
    title: 'Network',
    icon: r.netType == 'cellular' ? Icons.signal_cellular_alt : r.netType == 'ethernet' ? Icons.settings_ethernet : Icons.wifi,
    value: '↓ ${fmtRate(r.netRxBps)}',
    sub: '↑ ${fmtRate(r.netTxBps)}  ·  ${r.netType ?? '-'}',
    level: r.netType == 'none' ? Level.fault : networkLevel,
    series: [
      Series('Download', 'rx', (x) => x.netRxBps, rate: true),
      Series('Upload', 'tx', (x) => x.netTxBps, rate: true),
    ],
    networkChecks: networkChecks,
    facts: [
      ('Connection', r.netType ?? 'Not reported'),
      ('Download', fmtRate(r.netRxBps)),
      ('Upload', fmtRate(r.netTxBps)),
      for (final reading in networkDetails) (reading.label ?? reading.key, readingText(reading)),
    ],
  ));

  // Links: Tailscale and Syncthing, and how the other devices look from here.
  final peers = r.peers.entries.where((e) => !away.contains(e.key)).toList();
  final resting = r.peers.entries.where((e) => away.contains(e.key)).toList();
  final reach = peers.where((e) => e.value.reachable).length;
  final tailscale = readings['ts.state'];
  final syncthing = readings['st.state'];
  final reportedLinkLevel = _readingLevel([?tailscale, ?syncthing]);
  final linkLevel = reportedLinkLevel != Level.ok ? reportedLinkLevel
      : r.syncthingLocal == false || reach < peers.length ? Level.warn
      : r.tailscaleIp == null && tailscale?.value == null && r.syncthingLocal == null && syncthing?.value == null && peers.isEmpty
          ? Level.inactive : Level.ok;
  final tailState = tailscale?.value == null ? null : readingText(tailscale!);
  final syncState = syncthing?.value == null ? null : readingText(syncthing!);
  final tailDetails = [?tailState, ?r.tailscaleIp].join(' · ');
  out.add(Part(
    id: 'links',
    title: 'Links',
    icon: Icons.hub_outlined,
    value: peers.isEmpty ? '-' : '$reach of ${peers.length}',
    sub: tailscale?.level == 'fault' || tailscale?.level == 'warn'
        ? 'Tailscale · ${tailState ?? 'Not reported'}'
        : syncthing?.level == 'fault' || syncthing?.level == 'warn'
            ? 'Syncthing · ${syncState ?? 'Not reported'}'
            : r.syncthingLocal == false
            ? 'Syncthing not running here'
            : peers.isEmpty
                ? (linkLevel == Level.inactive ? 'Link status not reported' : 'No peer checks reported')
                : reach < peers.length
                    ? 'no answer from ${peers.where((e) => !e.value.reachable).map((e) => names[e.key] ?? e.key).join(', ')}'
                    : 'devices reachable',
    level: linkLevel,
    facts: [
      ('Tailscale', tailDetails.isEmpty ? 'Not reported' : tailDetails),
      ('Syncthing', syncState ?? (r.syncthingLocal == null ? 'Not reported' : r.syncthingLocal! ? 'Running' : 'Not running')),
      for (final e in peers)
        (
          names[e.key] ?? e.key,
          [
            e.value.pingMs != null ? '${e.value.pingMs} ms' : e.value.reachable ? 'reachable' : e.value.tcp == 'noip' ? 'no address' : 'no answer',
            switch (e.value.tcp) { 'open' => 'Syncthing up', 'refused' => 'Syncthing down', _ => null },
            if (e.value.seenAge != null) 'files ${ago(Duration(seconds: e.value.seenAge!))} old',
          ].whereType<String>().join(' · ')
        ),
      for (final e in resting) (names[e.key] ?? e.key, 'not running right now'),
    ],
  ));

  }

  // Extra readings, one part per group.
  final groups = <String, List<Reading>>{};
  for (final x in r.readings) {
    if (!r.isThing && x.group == 'Network' &&
        (_networkCheckKeys.contains(x.key) || _networkDetailKeys.contains(x.key))) {
      continue;
    }
    groups.putIfAbsent(x.group ?? 'Other', () => []).add(x);
  }
  for (final g in groups.entries) {
    final nums = g.value.where((x) => x.value is num).toList();
    out.add(Part(
      level: _readingLevel(g.value),
      id: 'g:${g.key}',
      title: g.key,
      icon: Icons.sensors,
      value: _headline(g.value),
      sub: g.value.length == 1 ? (g.value.first.label ?? g.value.first.key) : _subline(g.value),
      series: [
        for (final x in nums)
          Series(x.label ?? x.key, x.key, (rr) => rr.readings.where((y) => y.key == x.key).map((y) => y.value).whereType<num>().firstOrNull?.toDouble(),
              unit: x.unit ?? ''),
      ],
      facts: [for (final x in g.value) (x.label ?? x.key, readingText(x))],
    ));
  }
  return out;
}

/// The most telling reading of a group: a flagged one, else the first
/// number, else the first value.
String _headline(List<Reading> g) {
  final x = g.where((r) => r.level == 'fault').firstOrNull ??
      g.where((r) => r.level == 'warn').firstOrNull ??
      (g.length == 1 ? g.first : null);
  if (x != null) return readingText(x);
  return readingText(g.first);
}

String? _subline(List<Reading> g) {
  final rest = g.skip(1).take(2).map((r) => '${r.label ?? r.key} ${readingText(r)}');
  return rest.isEmpty ? null : rest.join('  ·  ');
}

int? _minus(int? a, int? b) => a == null || b == null ? null : a - b;

Level _readingLevel(Iterable<Reading> readings) {
  if (readings.any((reading) => reading.level == 'fault')) return Level.fault;
  if (readings.any((reading) => reading.level == 'warn')) return Level.warn;
  return Level.ok;
}

String readingText(Reading x) {
  final v = x.value;
  if (v == null) return x.note == null ? 'not available' : 'not available: ${x.note}';
  final t = v is double ? (v.abs() >= 100 ? v.round().toString() : v.toStringAsFixed(1)) : v is bool ? (v ? 'yes' : 'no') : '$v';
  final s = x.unit == null ? t : '$t ${x.unit}';
  // Only measured values go unmarked.
  return x.src == 'estimated' ? '$s (estimated)' : x.src == 'declared' ? '$s (declared)' : s;
}
