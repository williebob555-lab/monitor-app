import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';

import 'state.dart';
import 'theme.dart';
import 'widgets.dart';

/// One device: its status in words, its readings, today's history and what
/// it sees of the other devices. History is read only while this page is open.
class DevicePage extends StatefulWidget {
  const DevicePage(this.state, this.id, {super.key});
  final AppState state;
  final String id;
  @override
  State<DevicePage> createState() => _DevicePageState();
}

class _DevicePageState extends State<DevicePage> {
  List<Map<String, Object?>> history = const [];
  int _historySeq = -1;
  bool showLog = false;
  bool showFields = false;
  String filter = '';

  HistoryRange range = HistoryRange.day;
  HistoryRange? _loadedRange;

  // Read only when this page is open, and again only when the device wrote
  // something new or the range changed.
  void _loadHistory(Report r) {
    if (r.seq == _historySeq && range == _loadedRange) return;
    _historySeq = r.seq;
    _loadedRange = range;
    try {
      history = readHistoryRange(widget.state.folder.deviceDir(r.id), range, DateTime.now());
    } catch (_) {
      history = const [];
    }
  }

  @override
  Widget build(BuildContext context) {
    return ListenableBuilder(
      listenable: widget.state,
      builder: (context, _) {
        final a = widget.state.assessment;
        final s = a.devices.where((d) => d.report.id == widget.id).firstOrNull;
        if (s == null) {
          return Scaffold(appBar: AppBar(), body: const Center(child: Text('This device has no readings yet.')));
        }
        final r = s.report;
        _loadHistory(r);
        final names = {for (final d in a.devices) d.report.id: d.report.name};
        return Scaffold(
          appBar: AppBar(
            title: Row(children: [
              Icon(kindIcon(r.kind, r.name), color: C.muted, size: 22),
              const SizedBox(width: 10),
              Flexible(child: Text(r.name, overflow: TextOverflow.ellipsis)),
            ]),
          ),
          body: LayoutBuilder(builder: (context, box) {
            final wide = box.maxWidth > 820;
            final pad = wide ? 24.0 : 14.0;
            final left = <Widget>[
              _statusCard(s),
              const SizedBox(height: 14),
              _now(r),
              if (r.readings.isNotEmpty) ...[
                const SizedBox(height: 14),
                _extra(r),
              ],
              const SizedBox(height: 14),
              _history(r),
            ];
            final right = <Widget>[
              _peers(r, names),
              const SizedBox(height: 14),
              _about(r, s),
              const SizedBox(height: 14),
              _fields(r),
              const SizedBox(height: 14),
              _log(r),
              if (r.id != widget.state.host.id) ...[
                const SizedBox(height: 14),
                Align(
                  alignment: Alignment.centerLeft,
                  child: OutlinedButton.icon(
                    onPressed: () {
                      widget.state.hide(r.id);
                      Navigator.of(context).pop();
                    },
                    icon: const Icon(Icons.visibility_off_outlined, size: 18),
                    label: const Text('Hide on this device'),
                  ),
                ),
                const Padding(
                  padding: EdgeInsets.only(top: 6),
                  child: Text('Nothing is deleted. It comes back by itself if it reports again.',
                      style: TextStyle(color: C.muted, fontSize: 12)),
                ),
              ],
            ];
            return ListView(padding: EdgeInsets.fromLTRB(pad, 4, pad, 28), children: [
              if (wide)
                Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
                  Expanded(flex: 3, child: Column(children: left)),
                  const SizedBox(width: 14),
                  Expanded(flex: 2, child: Column(children: right)),
                ])
              else ...[
                ...left,
                const SizedBox(height: 14),
                ...right,
              ],
            ]);
          }),
        );
      },
    );
  }

  Widget _statusCard(DeviceStatus s) => Container(
        padding: const EdgeInsets.all(16),
        decoration: BoxDecoration(
          color: C.of(s.level).withValues(alpha: 0.08),
          borderRadius: BorderRadius.circular(14),
          border: Border.all(color: C.of(s.level).withValues(alpha: 0.35)),
        ),
        child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          StatusChip(s.level, s.title),
          const SizedBox(height: 10),
          Text(s.detail, style: const TextStyle(height: 1.4)),
          const SizedBox(height: 6),
          Text('Last update ${ago(s.age)} ago. Next one expected within ${ago(Duration(seconds: s.report.nextSeconds))}.',
              style: const TextStyle(color: C.muted, fontSize: 12)),
        ]),
      );

  Widget _now(Report r) {
    final b = r.battery;
    return Section(
      'Right now',
      Column(children: [
        if (b?.pct != null) ...[
          Meter('Battery', b!.pct, inverse: true,
              suffix: [
                if (b.charging == true) 'charging' else if (b.plugged == true) 'plugged in' else 'on battery',
                if (b.tempC != null) '${b.tempC!.toStringAsFixed(1)} °C',
              ].join(' · ')),
          const SizedBox(height: 14),
        ],
        Meter('CPU', r.cpuPct, note: r.cpuNote != null ? 'not available on Android' : null),
        const SizedBox(height: 14),
        Meter('Memory', pct(r.memUsed, r.memTotal), suffix: '${fmtBytes(r.memUsed)} of ${fmtBytes(r.memTotal)}'),
        const SizedBox(height: 14),
        Meter('Disk', pct(r.diskUsed, r.diskTotal), suffix: '${fmtBytes(r.diskUsed)} of ${fmtBytes(r.diskTotal)}'),
        const SizedBox(height: 14),
        Row(children: [
          const Text('Network', style: TextStyle(color: C.muted, fontSize: 12)),
          const Spacer(),
          Text('${r.netType ?? '-'}   ↓ ${fmtRate(r.netRxBps)}   ↑ ${fmtRate(r.netTxBps)}', style: const TextStyle(fontSize: 12)),
        ]),
      ]),
    );
  }

  /// Any extra readings a device publishes, grouped. Nothing here is special
  /// cased: new kinds of readings show up without an app change.
  Widget _extra(Report r) {
    final groups = <String, List<Reading>>{};
    for (final x in r.readings) {
      groups.putIfAbsent(x.group ?? 'Other', () => []).add(x);
    }
    String fmt(Reading x) {
      final v = x.value;
      final t = v is double ? (v.abs() >= 100 ? v.round().toString() : v.toStringAsFixed(1)) : v is bool ? (v ? 'yes' : 'no') : '$v';
      return x.unit == null ? t : '$t ${x.unit}';
    }

    return Column(children: [
      for (final g in groups.entries) ...[
        Section(
          g.key,
          Column(children: [
            for (final x in g.value)
              Padding(
                padding: const EdgeInsets.only(bottom: 8),
                child: Row(children: [
                  Expanded(child: Text(x.label ?? x.key, style: const TextStyle(color: C.muted, fontSize: 12.5))),
                  Text(fmt(x), style: const TextStyle(fontSize: 12.5)),
                ]),
              ),
          ]),
        ),
        if (g.key != groups.keys.last) const SizedBox(height: 14),
      ],
    ]);
  }

  Widget _history(Report r) {
    List<double?> series(String k) => [for (final l in history) chartValue(l, k)];
    final rows = <(String, List<double?>, double?, Color)>[
      if (r.battery?.pct != null) ('Battery %', series('b'), 100, C.ok),
      if (r.cpuPct != null) ('CPU %', series('cpu'), 100, C.flow),
      ('Memory %', series('mem'), 100, C.flow),
      ('Download', series('rx'), null, C.flow),
      ('Upload', series('tx'), null, C.flow),
      // Any extra numeric reading the device records in its history.
      for (final x in r.readings)
        if (x.value is num) ('${x.label ?? x.key}${x.unit != null ? ' (${x.unit})' : ''}', series(x.key), null, C.flow),
    ];
    return Section(
      'History',
      trailing: SegmentedButton<HistoryRange>(
        showSelectedIcon: false,
        style: const ButtonStyle(visualDensity: VisualDensity.compact),
        segments: const [
          ButtonSegment(value: HistoryRange.day, label: Text('24 h')),
          ButtonSegment(value: HistoryRange.week, label: Text('7 days')),
          ButtonSegment(value: HistoryRange.year, label: Text('Year')),
        ],
        selected: {range},
        onSelectionChanged: (v) => setState(() => range = v.first),
      ),
      history.length < 2
          ? Text(
              switch (range) {
                HistoryRange.day => 'Readings are added every 15 minutes, so the first ones appear here within about 15 minutes.',
                HistoryRange.week => 'Hourly summaries appear after the first full hour.',
                HistoryRange.year => 'Daily summaries appear after the first full day.',
              },
              style: const TextStyle(color: C.muted))
          : Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
              for (final row in rows) ...[
                Row(children: [
                  Text(row.$1, style: const TextStyle(color: C.muted, fontSize: 12)),
                  const Spacer(),
                  Text(_last(row.$2, row.$1), style: const TextStyle(fontSize: 12)),
                ]),
                const SizedBox(height: 4),
                Spark(row.$2, max: row.$3, color: row.$4, height: 44),
                const SizedBox(height: 12),
              ],
              Text(range == HistoryRange.day ? '${history.length} readings' : '${history.length} ${range == HistoryRange.week ? 'hourly' : 'daily'} averages',
                  style: const TextStyle(color: C.muted, fontSize: 11)),
            ]),
    );
  }

  String _last(List<double?> s, String label) {
    final v = s.lastWhere((x) => x != null, orElse: () => null);
    if (v == null) return '-';
    if (label.endsWith('%')) return '${v.round()}%';
    if (label == 'Download' || label == 'Upload') return fmtRate(v);
    return v.abs() >= 100 ? '${v.round()}' : v.toStringAsFixed(1);
  }

  Widget _peers(Report r, Map<String, String> names) {
    final entries = r.peers.entries.toList()..sort((a, b) => (names[a.key] ?? a.key).compareTo(names[b.key] ?? b.key));
    return Section(
      'What it sees',
      entries.isEmpty
          ? const Text('It has not checked any other device yet.', style: TextStyle(color: C.muted))
          : Column(children: [
              for (final e in entries) _peerRow(names[e.key] ?? e.key, e.value),
            ]),
    );
  }

  Widget _peerRow(String name, PeerView v) {
    final ts = v.pingMs != null
        ? 'reachable, ${v.pingMs} ms'
        : v.reachable
            ? 'reachable'
            : v.tcp == 'noip'
                ? 'no Tailscale address'
                : 'no answer';
    final st = switch (v.tcp) {
      'open' => 'listening',
      'refused' => 'not running',
      _ => 'unknown',
    };
    final files = v.seenAge == null ? 'never arrived' : '${ago(Duration(seconds: v.seenAge!))} old';
    final lvl = v.reachable ? (v.tcp == 'refused' ? Level.warn : Level.ok) : Level.fault;
    return Padding(
      padding: const EdgeInsets.only(bottom: 12),
      child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
        Padding(padding: const EdgeInsets.only(top: 5), child: StatusDot(lvl, size: 8)),
        const SizedBox(width: 10),
        Expanded(
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Text(name, style: const TextStyle(fontWeight: FontWeight.w600)),
            const SizedBox(height: 2),
            Text('Tailscale: $ts\nSyncthing: $st\nIts readings here: $files',
                style: const TextStyle(color: C.muted, fontSize: 12, height: 1.4)),
          ]),
        ),
      ]),
    );
  }

  Widget _about(Report r, DeviceStatus s) {
    final self = r.self;
    String row(String k, String? v) => '$k: ${v ?? '-'}';
    return Section(
      'About',
      SelectableText(
        [
          row('System', r.os),
          row('App version', r.app),
          row('Tailscale address', r.tailscaleIp ?? 'none'),
          row('Syncthing on it', r.syncthingLocal == null ? null : (r.syncthingLocal! ? 'running' : 'not running')),
          if (r.screenOn != null) row('Screen', r.screenOn! ? 'on' : 'off'),
          if (r.bootTime != null) row('Up for', ago(DateTime.now().toUtc().difference(r.bootTime!))),
          row('Collector cost',
              self == null ? null : '${self.runMs ?? '-'} ms per run, ${self.cpuMs ?? '-'} ms CPU, ${self.rssMb?.toStringAsFixed(1) ?? '-'} MB'),
          if (self != null && self.over.isNotEmpty) 'Over budget: ${self.over.join('; ')}',
          row('Device id', r.id),
        ].join('\n'),
        style: const TextStyle(color: C.muted, fontSize: 12.5, height: 1.6),
      ),
    );
  }

  /// Every field in the device's file, by path. Shows fields this version of
  /// the app doesn't know about yet, too; filter by typing part of a path.
  Widget _fields(Report r) {
    final all = r.flatten().entries.where((e) => filter.isEmpty || e.key.toLowerCase().contains(filter.toLowerCase())).toList();
    return Section(
      'All fields',
      !showFields
          ? Text('${r.flatten().length} fields in its file, format version ${r.formatVersion}.',
              style: const TextStyle(color: C.muted, fontSize: 12))
          : Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
              TextField(
                decoration: const InputDecoration(isDense: true, hintText: 'Filter, e.g. battery or peers', border: OutlineInputBorder()),
                style: const TextStyle(fontSize: 13),
                onChanged: (v) => setState(() => filter = v),
              ),
              const SizedBox(height: 10),
              SelectableText(
                all.map((e) => '${e.key} = ${e.value}').join('\n'),
                style: const TextStyle(fontFamily: 'monospace', fontSize: 11, color: C.muted, height: 1.5),
              ),
            ]),
      trailing: TextButton(onPressed: () => setState(() => showFields = !showFields), child: Text(showFields ? 'Hide' : 'Show')),
    );
  }

  Widget _log(Report r) {
    return Section(
      'Its log',
      showLog
          ? SelectableText(
              _tail(widget.state.folder.readLog(r.id), 80),
              style: const TextStyle(fontFamily: 'monospace', fontSize: 11, color: C.muted, height: 1.4),
            )
          : const Text('The app writes what it does here, and it syncs like everything else.',
              style: TextStyle(color: C.muted, fontSize: 12)),
      trailing: TextButton(onPressed: () => setState(() => showLog = !showLog), child: Text(showLog ? 'Hide' : 'Show')),
    );
  }

  String _tail(String s, int lines) {
    final l = s.trimRight().split('\n');
    return l.length <= lines ? l.join('\n') : l.sublist(l.length - lines).join('\n');
  }
}
