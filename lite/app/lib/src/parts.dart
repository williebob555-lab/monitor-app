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

List<Part> partsOf(Report r, Map<String, String> names) {
  final out = <Part>[];
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
  out.add(Part(
    id: 'network',
    title: 'Network',
    icon: r.netType == 'cellular' ? Icons.signal_cellular_alt : r.netType == 'ethernet' ? Icons.settings_ethernet : Icons.wifi,
    value: '↓ ${fmtRate(r.netRxBps)}',
    sub: '↑ ${fmtRate(r.netTxBps)}  ·  ${r.netType ?? '-'}',
    level: r.netType == 'none' ? Level.fault : Level.ok,
    series: [
      Series('Download', 'rx', (x) => x.netRxBps, rate: true),
      Series('Upload', 'tx', (x) => x.netTxBps, rate: true),
    ],
    facts: [('Connection', r.netType ?? '-'), ('Download', fmtRate(r.netRxBps)), ('Upload', fmtRate(r.netTxBps))],
  ));

  // Links: Tailscale and Syncthing, and how the other devices look from here.
  final peers = r.peers.entries.toList();
  final reach = peers.where((e) => e.value.reachable).length;
  final linkLevel = r.tailscaleIp == null
      ? Level.fault
      : r.syncthingLocal == false
          ? Level.warn
          : peers.isNotEmpty && reach < peers.length
              ? Level.warn
              : Level.ok;
  out.add(Part(
    id: 'links',
    title: 'Links',
    icon: Icons.hub_outlined,
    value: peers.isEmpty ? '-' : '$reach of ${peers.length}',
    sub: r.tailscaleIp == null
        ? 'Tailscale is off'
        : r.syncthingLocal == false
            ? 'Syncthing not running here'
            : peers.isEmpty
                ? 'no other devices yet'
                : 'devices reachable',
    level: linkLevel,
    facts: [
      ('Tailscale', r.tailscaleIp ?? 'off (no address)'),
      ('Syncthing', r.syncthingLocal == null ? '-' : r.syncthingLocal! ? 'running' : 'not running'),
      for (final e in peers)
        (
          names[e.key] ?? e.key,
          [
            e.value.pingMs != null ? '${e.value.pingMs} ms' : e.value.reachable ? 'reachable' : e.value.tcp == 'noip' ? 'no address' : 'no answer',
            switch (e.value.tcp) { 'open' => 'Syncthing up', 'refused' => 'Syncthing down', _ => null },
            if (e.value.seenAge != null) 'files ${ago(Duration(seconds: e.value.seenAge!))} old',
          ].whereType<String>().join(' · ')
        ),
    ],
  ));

  // Extra readings, one part per group.
  final groups = <String, List<Reading>>{};
  for (final x in r.readings) {
    groups.putIfAbsent(x.group ?? 'Other', () => []).add(x);
  }
  for (final g in groups.entries) {
    final nums = g.value.where((x) => x.value is num).toList();
    out.add(Part(
      id: 'g:${g.key}',
      title: g.key,
      icon: Icons.sensors,
      value: g.value.length == 1 ? _readingText(g.value.first) : '${g.value.length} readings',
      sub: g.value.length == 1 ? (g.value.first.label ?? g.value.first.key) : null,
      series: [
        for (final x in nums)
          Series(x.label ?? x.key, x.key, (rr) => rr.readings.where((y) => y.key == x.key).map((y) => y.value).whereType<num>().firstOrNull?.toDouble(),
              unit: x.unit ?? ''),
      ],
      facts: [for (final x in g.value) (x.label ?? x.key, _readingText(x))],
    ));
  }
  return out;
}

int? _minus(int? a, int? b) => a == null || b == null ? null : a - b;

String _readingText(Reading x) {
  final v = x.value;
  final t = v is double ? (v.abs() >= 100 ? v.round().toString() : v.toStringAsFixed(1)) : v is bool ? (v ? 'yes' : 'no') : '$v';
  return x.unit == null ? t : '$t ${x.unit}';
}
