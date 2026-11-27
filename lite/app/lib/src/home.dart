import 'dart:math' as math;

import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';

import 'device_page.dart';
import 'dive.dart';
import 'network_map_panel.dart';
import 'parts.dart';
import 'setup_page.dart';
import 'state.dart';
import 'theme.dart';
import 'widgets.dart';

class HomePage extends StatelessWidget {
  const HomePage(this.state, {super.key});
  final AppState state;

  @override
  Widget build(BuildContext context) {
    return ListenableBuilder(
      listenable: state,
      builder: (context, _) {
        final a = state.assessment;
        final setupNeeded = !state.folder.exists || state.reports[state.host.id] == null;
        return Scaffold(
          appBar: AppBar(
            titleSpacing: 20,
            title: const Text('Monitor', style: TextStyle(fontWeight: FontWeight.w700, letterSpacing: 0.3)),
            actions: [
              if (state.update != null)
                Padding(
                  padding: const EdgeInsets.only(right: 4),
                  child: TextButton.icon(
                    onPressed: () => _openSetup(context),
                    icon: const Icon(Icons.system_update_alt, size: 18),
                    label: Text('Update ${state.update!.version}'),
                  ),
                ),
              IconButton(
                tooltip: state.showMap ? 'Show as cards' : 'Show as a map',
                onPressed: () => state.setShowMap(!state.showMap),
                icon: Icon(state.showMap ? Icons.grid_view_rounded : Icons.hub_outlined),
              ),
              IconButton(
                tooltip: 'Setup and updates',
                onPressed: () => _openSetup(context),
                icon: Badge(isLabelVisible: setupNeeded, smallSize: 8, child: const Icon(Icons.tune)),
              ),
              const SizedBox(width: 8),
            ],
          ),
          body: RefreshIndicator(
            onRefresh: () async {
              await state.host.nudge();
              state.refresh(force: true);
            },
            child: LayoutBuilder(builder: (context, box) {
              final pad = box.maxWidth > 700 ? 24.0 : 14.0;
              return CustomScrollView(slivers: [
                SliverPadding(
                  padding: EdgeInsets.fromLTRB(pad, 4, pad, 14),
                  sliver: SliverToBoxAdapter(
                    child: _Banner(a.network, setupNeeded: setupNeeded, onSetup: () => _openSetup(context), attention: _attention(a), onOpen: (d, p) {
                      Navigator.of(context).push(MaterialPageRoute<void>(builder: (_) => PartPage(state, d, p)));
                    }),
                  ),
                ),
                if (state.showMap)
                  // The map takes the whole width and the rest of the screen
                  // below the banner (at least enough to read on a phone).
                  SliverLayoutBuilder(builder: (context, c) {
                    final rest = c.viewportMainAxisExtent - c.precedingScrollExtent - 24;
                    return SliverPadding(
                      padding: EdgeInsets.fromLTRB(pad, 0, pad, 24),
                      sliver: SliverToBoxAdapter(
                        child: SizedBox(height: math.max(rest, 420), child: MapPanel(
                          folder: state.folder,
                          devices: a.devices,
                          things: [for (final t in state.things.values) state.statusOf(t.id)!],
                          myId: state.host.id,
                          openLive: (id) => Navigator.of(context).push(
                            MaterialPageRoute<void>(builder: (_) => DevicePage(state, id)),
                          ),
                        )),
                      ),
                    );
                  })
                else
                SliverPadding(
                  padding: EdgeInsets.fromLTRB(pad, 0, pad, 24),
                  sliver: SliverGrid(
                    gridDelegate: const SliverGridDelegateWithMaxCrossAxisExtent(
                      maxCrossAxisExtent: 420,
                      mainAxisExtent: 262,
                      mainAxisSpacing: 14,
                      crossAxisSpacing: 14,
                    ),
                    delegate: SliverChildBuilderDelegate(
                      (context, i) => DeviceCard(a.devices[i], isMe: a.devices[i].report.id == state.host.id, onTap: () {
                        Navigator.of(context).push(MaterialPageRoute<void>(builder: (_) => DevicePage(state, a.devices[i].report.id)));
                      }),
                      childCount: a.devices.length,
                    ),
                  ),
                ),
                if (state.hiddenReports.isNotEmpty)
                  SliverPadding(
                    padding: EdgeInsets.fromLTRB(pad, 0, pad, 28),
                    sliver: SliverToBoxAdapter(
                      child: Section(
                        'Hidden on this device',
                        Column(children: [
                          for (final r in state.hiddenReports)
                            Row(children: [
                              Icon(kindIcon(r.kind, r.name), color: C.muted, size: 18),
                              const SizedBox(width: 10),
                              Expanded(
                                child: Text('${r.name}  ·  last seen ${ago(DateTime.now().toUtc().difference(r.time))} ago',
                                    style: const TextStyle(color: C.muted)),
                              ),
                              TextButton(onPressed: () => state.unhide(r.id), child: const Text('Show')),
                            ]),
                        ]),
                      ),
                    ),
                  ),
              ]);
            }),
          ),
        );
      },
    );
  }

  /// Parts that need a look on devices that are reporting now, worst first.
  List<(String, Part, Report)> _attention(Assessment a) {
    final names = {for (final d in a.devices) d.report.id: d.report.name};
    final away = {for (final d in a.devices) if (d.level == Level.inactive) d.report.id};
    final out = <(String, Part, Report)>[];
    for (final d in a.devices) {
      if (!d.fresh) continue;
      for (final p in partsOf(d.report, names, away: away)) {
        if (p.level == Level.warn || p.level == Level.fault) out.add((d.report.id, p, d.report));
      }
    }
    out.sort((x, y) => (y.$2.level == Level.fault ? 1 : 0).compareTo(x.$2.level == Level.fault ? 1 : 0));
    return out;
  }

  void _openSetup(BuildContext context) =>
      Navigator.of(context).push(MaterialPageRoute<void>(builder: (_) => SetupPage(state)));
}

class _Banner extends StatelessWidget {
  const _Banner(this.n, {required this.setupNeeded, required this.onSetup, required this.attention, required this.onOpen});
  final NetworkStatus n;
  final bool setupNeeded;
  final VoidCallback onSetup;
  final List<(String, Part, Report)> attention;
  final void Function(String device, String part) onOpen;

  @override
  Widget build(BuildContext context) {
    final color = C.of(n.level);
    return Container(
      padding: const EdgeInsets.fromLTRB(18, 16, 18, 16),
      decoration: BoxDecoration(
        color: C.surface,
        borderRadius: BorderRadius.circular(14),
        border: Border(left: BorderSide(color: color, width: 4), top: const BorderSide(color: C.line), right: const BorderSide(color: C.line), bottom: const BorderSide(color: C.line)),
      ),
      child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
        Padding(padding: const EdgeInsets.only(top: 6), child: StatusDot(n.level, size: 12)),
        const SizedBox(width: 14),
        Expanded(
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Text(n.title, style: const TextStyle(fontSize: 18, fontWeight: FontWeight.w700)),
            if (n.detail.isNotEmpty) ...[
              const SizedBox(height: 4),
              Text(n.detail, style: const TextStyle(color: C.muted, height: 1.35)),
            ],
            if (attention.isNotEmpty) ...[
              const SizedBox(height: 8),
              for (final x in attention.take(4))
                InkWell(
                  onTap: () => onOpen(x.$1, x.$2.id),
                  child: Padding(
                    padding: const EdgeInsets.symmetric(vertical: 4),
                    child: Row(children: [
                      Icon(x.$2.level == Level.fault ? Icons.error_outline : Icons.warning_amber_rounded, size: 16, color: C.of(x.$2.level)),
                      const SizedBox(width: 8),
                      Expanded(
                        child: Text(x.$2.id == 'links' ? '${x.$3.name} · ${x.$2.sub}' : '${x.$3.name} · ${x.$2.title}: ${x.$2.value}${x.$2.sub != null ? ' (${x.$2.sub})' : ''}',
                            maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 13)),
                      ),
                      const Icon(Icons.chevron_right, size: 18, color: C.muted),
                    ]),
                  ),
                ),
              if (attention.length > 4)
                Text('and ${attention.length - 4} more', style: const TextStyle(color: C.muted, fontSize: 12)),
            ],
            if (setupNeeded) ...[
              const SizedBox(height: 10),
              FilledButton.tonal(onPressed: onSetup, child: const Text('Finish setup on this device')),
            ],
          ]),
        ),
      ]),
    );
  }
}

class DeviceCard extends StatelessWidget {
  const DeviceCard(this.s, {super.key, required this.isMe, required this.onTap});
  final DeviceStatus s;
  final bool isMe;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final r = s.report;
    final dim = s.level == Level.inactive || !s.fresh;
    return Card(
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        onTap: onTap,
        child: Padding(
          padding: const EdgeInsets.fromLTRB(16, 14, 16, 14),
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Row(children: [
              Icon(kindIcon(r.kind, r.name), color: C.muted, size: 20),
              const SizedBox(width: 10),
              Expanded(
                child: Text(r.name,
                    maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 16, fontWeight: FontWeight.w600)),
              ),
              if (isMe) const Text('THIS DEVICE', style: TextStyle(color: C.muted, fontSize: 10.5, letterSpacing: 1)),
            ]),
            const SizedBox(height: 10),
            StatusChip(s.level, s.title),
            const SizedBox(height: 8),
            Text(s.detail, maxLines: 2, overflow: TextOverflow.ellipsis, style: const TextStyle(color: C.muted, fontSize: 12.5, height: 1.3)),
            const Spacer(),
            Opacity(
              opacity: dim ? 0.55 : 1,
              child: Column(children: [
                Row(children: [
                  Expanded(
                    child: r.battery?.pct != null
                        ? Meter('Battery', r.battery!.pct, inverse: true, suffix: r.battery!.charging == true ? 'charging' : null)
                        : Meter('CPU', r.cpuPct, note: r.kind == 'android' ? 'not on Android' : null),
                  ),
                  const SizedBox(width: 16),
                  Expanded(child: Meter('Memory', pct(r.memUsed, r.memTotal))),
                ]),
                const SizedBox(height: 10),
                Row(children: [
                  Expanded(
                    child: r.battery?.pct != null
                        ? Meter('CPU', r.cpuPct, note: r.kind == 'android' ? 'not on Android' : null)
                        : Meter('Disk', pct(r.diskUsed, r.diskTotal)),
                  ),
                  const SizedBox(width: 16),
                  Expanded(child: _Net(r)),
                ]),
              ]),
            ),
            const SizedBox(height: 8),
            Row(children: [
              if (r.live && s.fresh) ...[
                const Icon(Icons.fiber_manual_record, size: 9, color: C.ok),
                const SizedBox(width: 4),
                Text('LIVE · every ${r.nextSeconds} s  ·  ', style: const TextStyle(color: C.ok, fontSize: 11)),
              ],
              Expanded(
                child: Text('Updated ${ago(s.age)} ago${r.app != null ? '  ·  v${r.app}' : ''}',
                    maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(color: C.muted, fontSize: 11)),
              ),
            ]),
          ]),
        ),
      ),
    );
  }
}

class _Net extends StatelessWidget {
  const _Net(this.r);
  final Report r;
  @override
  Widget build(BuildContext context) => Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
        Row(children: [
          Text('Network${r.netType != null && r.netType != 'none' ? ' · ${r.netType}' : ''}',
              style: const TextStyle(color: C.muted, fontSize: 12)),
        ]),
        const SizedBox(height: 3),
        Text('↓ ${fmtRate(r.netRxBps)}   ↑ ${fmtRate(r.netTxBps)}',
            maxLines: 1, overflow: TextOverflow.ellipsis,
            style: const TextStyle(fontSize: 12, fontFeatures: [FontFeature.tabularFigures()])),
      ]);
}
