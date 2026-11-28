import 'dart:isolate';

import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';
import 'package:url_launcher/url_launcher.dart';

import 'controls_view.dart';
import 'disk_health.dart';
import 'dive.dart';
import 'parts.dart';
import 'state.dart';
import 'theme.dart';
import 'widgets.dart';

/// One device, opened up: its status in words, then the device with its parts
/// around it (tap a part to dive into it), then what it is and its log.
class DevicePage extends StatefulWidget {
  const DevicePage(this.state, this.id, {super.key});
  final AppState state;
  final String id;
  @override
  State<DevicePage> createState() => _DevicePageState();
}

class _DevicePageState extends State<DevicePage> {
  bool showLog = false;
  bool showFields = false;
  String filter = '';
  Future<List<Pattern>>? patterns;

  /// Worked out once per opening, off the UI thread: it reads up to a week
  /// of this device's history and a month of every device's summaries.
  Future<List<Pattern>> _findPatterns() {
    final st = widget.state;
    final names = st.names;
    final dir = st.dirOf(widget.id);
    final others = {for (final id in st.reports.keys) if (id != widget.id) id: st.folder.deviceDir(id)};
    return Isolate.run(() {
      final now = DateTime.now();
      final up = {for (final e in others.entries) e.key: hoursUp(readHourly(e.value, now))};
      return findPatterns(readDetail(dir, now), readHourly(dir, now), names: names, upHours: up);
    });
  }

  @override
  Widget build(BuildContext context) {
    return ListenableBuilder(
      listenable: widget.state,
      builder: (context, _) {
        final st = widget.state;
        final s = st.statusOf(widget.id);
        if (s == null) {
          return Scaffold(appBar: AppBar(), body: const Center(child: Text('This device has no readings yet.')));
        }
        final r = s.report;
        final names = st.names;
        final attached = st.thingsOf(r.id);
        return Scaffold(
          appBar: AppBar(
            title: Row(children: [
              Icon(r.isThing ? thingIcon(r.node?.type) : kindIcon(r.kind, r.name), color: C.muted, size: 22),
              const SizedBox(width: 10),
              Flexible(child: Text(r.name, overflow: TextOverflow.ellipsis)),
            ]),
          ),
          body: LayoutBuilder(builder: (context, box) {
            final wide = box.maxWidth > 820;
            final pad = wide ? 24.0 : 14.0;
            final parts = partsOf(r, names, away: widget.state.away);
            final hub = DeviceHub(
              status: s,
              parts: parts,
              live: widget.state.live[r.id] ?? const [],
              onOpen: (p) => Navigator.of(context).push(MaterialPageRoute<void>(builder: (_) => PartPage(widget.state, r.id, p.id))),
            );
            final left = <Widget>[
              if (_url(r) != null) ...[_open(_url(r)!), const SizedBox(height: 14)],
              if (attached.isNotEmpty) ...[_attached(attached, docker: attached.every((t) => t.report.node?.type == 'container')), const SizedBox(height: 14)],
              if (r.isThing) ...[_connections(r, names), const SizedBox(height: 14)],
              if (r.isThing && !const {'container', 'compose-project'}.contains(r.node?.type)) _params(r) else if (!r.isThing) _about(r, s),
              if (!r.isThing) ...[const SizedBox(height: 14), _patterns()],
            ];
            final right = <Widget>[
              if (r.controls.isNotEmpty) ...[ControlsSection(widget.state, r), const SizedBox(height: 14)],
              _fields(r),
              const SizedBox(height: 14),
              _log(r),
              if (!r.isThing && r.id != widget.state.host.id && widget.state.host.isAndroid) ...[
                const SizedBox(height: 14),
                _notify(r),
              ],
              if (!r.isThing && r.id != widget.state.host.id) ...[
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
              _statusCard(s),
              const SizedBox(height: 16),
              hub,
              const SizedBox(height: 8),
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

  /// Things plugged into this device, each one a way down the hierarchy.
  Widget _attached(List<Thing> things, {bool docker = false}) {
    return Section(
      docker ? 'Containers' : 'Attached',
      Column(children: [
        for (final t in things) _thingRow(t),
      ]),
    );
  }

  Widget _thingRow(Thing t) {
    final s = widget.state.statusOf(t.id)!;
    final vias = t.report.node?.links.where((l) => l.to == t.report.node?.parent).map((l) => l.via).toSet().join(' · ') ?? '';
    return InkWell(
      borderRadius: BorderRadius.circular(10),
      onTap: () => Navigator.of(context).push(MaterialPageRoute<void>(builder: (_) => DevicePage(widget.state, t.id))),
      child: Padding(
        padding: const EdgeInsets.symmetric(vertical: 8, horizontal: 4),
        child: Row(children: [
          Icon(thingIcon(t.report.node?.type), size: 20, color: s.level == Level.ok ? C.text : C.of(s.level)),
          const SizedBox(width: 12),
          Expanded(
            child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
              Text(t.report.name, style: const TextStyle(fontWeight: FontWeight.w600)),
              if (t.report.node?.type == 'disk')
                DiskHealth(s)
              else
                Text('${s.title}${vias.isEmpty ? '' : '  ·  $vias'}', style: TextStyle(color: s.level == Level.ok ? C.muted : C.of(s.level), fontSize: 12)),
            ]),
          ),
          const Icon(Icons.chevron_right, color: C.muted, size: 18),
        ]),
      ),
    );
  }

  /// A thing's place in the hierarchy, as its file declares it.
  Widget _connections(Report r, Map<String, String> names) {
    final n = r.node;
    String dir(String d) => switch (d) { 'in' => 'into it', 'out' => 'out of it', _ => 'both ways' };
    return Section(
      'Connections',
      SelectableText(
        [
          'Attached to: ${n?.parent == null ? '-' : names[n!.parent] ?? n.parent}',
          for (final l in n?.links ?? const <NodeLink>[]) '${l.via.toUpperCase()} with ${names[l.to] ?? l.to}, ${dir(l.dir)}${l.detail == null ? '' : ' (${l.detail})'}',
          'Id: ${r.id}',
        ].join('\n'),
        style: const TextStyle(color: C.muted, fontSize: 12.5, height: 1.6),
      ),
    );
  }

  /// What its history shows, in words (core patterns.dart).
  Widget _patterns() {
    patterns ??= _findPatterns();
    return FutureBuilder<List<Pattern>>(
      future: patterns,
      builder: (context, snap) {
        final list = snap.data;
        final Widget body;
        if (snap.hasError) {
          body = Text('Could not read its history: ${snap.error}', style: const TextStyle(color: C.muted, fontSize: 12.5));
        } else if (list == null) {
          body = const Text('Reading its history...', style: TextStyle(color: C.muted, fontSize: 12.5));
        } else if (list.isEmpty) {
          body = const Text('Not enough history yet. Patterns appear after a few hours of readings.',
              style: TextStyle(color: C.muted, fontSize: 12.5));
        } else {
          final groups = <String, List<Pattern>>{};
          for (final p in list) {
            groups.putIfAbsent(p.group, () => []).add(p);
          }
          body = Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            for (final g in groups.entries) ...[
              Padding(
                padding: const EdgeInsets.only(top: 6, bottom: 2),
                child: Text(g.key, style: const TextStyle(fontWeight: FontWeight.w600, fontSize: 13)),
              ),
              for (final p in g.value)
                Text('${p.text}${p.estimated ? ' (estimated)' : ''}',
                    style: TextStyle(color: p.estimated ? C.muted : C.text, fontSize: 12.5, height: 1.5)),
            ],
          ]);
        }
        return Section('Patterns', body);
      },
    );
  }

  /// Settings a thing's own software exported to params.json, shown as is.
  Widget _params(Report r) {
    final p = widget.state.things[r.id]?.params;
    return Section(
      'Its settings',
      p == null || p.isEmpty
          ? const Text('None exported. Software that controls it can write params.json in its folder, and it shows here.',
              style: TextStyle(color: C.muted, fontSize: 12))
          : SelectableText(p.entries.map((e) => '${e.key}: ${e.value}').join('\n'),
              style: const TextStyle(color: C.muted, fontSize: 12.5, height: 1.6)),
    );
  }

  static String? _url(Report r) {
    final v = r.readings.where((x) => x.key == 'ct.url').firstOrNull?.value;
    return v is String && v.startsWith(RegExp('https?://')) ? v : null;
  }

  /// A container's web interface, found by the collector (a port that
  /// answers, or its monitor.url label).
  Widget _open(String url) => Align(
        alignment: Alignment.centerLeft,
        child: FilledButton.tonalIcon(
          onPressed: () => launchUrl(Uri.parse(url), mode: LaunchMode.externalApplication),
          icon: const Icon(Icons.open_in_new, size: 18),
          label: Text('Open ${Uri.parse(url).host}:${Uri.parse(url).port}'),
        ),
      );

  Widget _notify(Report r) {
    final st = widget.state;
    final on = st.notify.contains(r.id);
    return Card(
      child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
        SwitchListTile(
          value: on,
          onChanged: (v) => st.setNotify(r.id, v),
          secondary: Icon(on ? Icons.notifications_active_outlined : Icons.notifications_none, color: C.muted),
          title: const Text('Notify me on this phone'),
          subtitle: Text('If ${r.name} stops reporting for 30 min, or its Syncthing folder is stuck for 15 min.',
              style: const TextStyle(color: C.muted, fontSize: 12)),
        ),
        if (on && !st.notificationsAllowed)
          const Padding(
            padding: EdgeInsets.fromLTRB(16, 0, 16, 12),
            child: Text('Notifications are off for Monitor App. Allow them in Android settings, or switch this off and on again to be asked.',
                style: TextStyle(color: C.warn, fontSize: 12)),
          ),
      ]),
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
