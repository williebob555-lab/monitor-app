import 'dart:math' as math;

import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';

import 'parts.dart';
import 'state.dart';
import 'theme.dart';
import 'widgets.dart';

/// The device "opened up": the device in the middle, its parts around it,
/// joined by lines. Each part shows its value and a live line; tap one to
/// dive into it.
class DeviceHub extends StatelessWidget {
  const DeviceHub({super.key, required this.status, required this.parts, required this.live, required this.onOpen});
  final DeviceStatus status;
  final List<Part> parts;
  final List<Report> live;
  final void Function(Part) onOpen;

  static const tileH = 104.0, gap = 12.0;

  @override
  Widget build(BuildContext context) {
    return LayoutBuilder(builder: (context, box) {
      final wide = box.maxWidth >= 760;
      return wide ? _wide(box.maxWidth) : _narrow(box.maxWidth);
    });
  }

  Widget _tile(Part p) => SizedBox(height: tileH, child: PartTile(p, live: live, onTap: () => onOpen(p)));

  Widget _center({double? width}) {
    final r = status.report;
    final color = C.of(status.level);
    return Container(
      width: width,
      padding: const EdgeInsets.fromLTRB(16, 14, 16, 14),
      decoration: BoxDecoration(
        color: C.surface,
        borderRadius: BorderRadius.circular(16),
        border: Border.all(color: color.withValues(alpha: 0.7), width: 1.5),
        boxShadow: [BoxShadow(color: color.withValues(alpha: 0.12), blurRadius: 20)],
      ),
      child: Column(mainAxisSize: MainAxisSize.min, children: [
        Icon(kindIcon(r.kind, r.name), size: 40, color: C.flow),
        const SizedBox(height: 8),
        Text(r.name, textAlign: TextAlign.center, style: const TextStyle(fontSize: 17, fontWeight: FontWeight.w700)),
        const SizedBox(height: 2),
        Text(r.os ?? r.kind, textAlign: TextAlign.center, maxLines: 2, style: const TextStyle(color: C.muted, fontSize: 11.5)),
        const SizedBox(height: 8),
        StatusChip(status.level, status.title),
      ]),
    );
  }

  /// Parts in two columns either side of the device, elbow lines between.
  Widget _wide(double w) {
    final left = <Part>[], right = <Part>[];
    for (var i = 0; i < parts.length; i++) {
      (i.isEven ? left : right).add(parts[i]);
    }
    final rows = math.max(left.length, right.length);
    final h = rows * tileH + (rows - 1) * gap;
    final colW = (w - 260 - 2 * 48) / 2;
    return SizedBox(
      height: math.max(h, 220),
      child: Stack(children: [
        Positioned.fill(child: CustomPaint(painter: _HubPainter(left: left.length, right: right.length, colW: colW, height: math.max(h, 220)))),
        Positioned(left: 0, top: 0, width: colW, child: Column(children: [for (final p in left) ...[_tile(p), const SizedBox(height: gap)]])),
        Positioned(right: 0, top: 0, width: colW, child: Column(children: [for (final p in right) ...[_tile(p), const SizedBox(height: gap)]])),
        Positioned(left: colW + 48, width: 260, top: 0, bottom: 0, child: Center(child: _center(width: 260))),
      ]),
    );
  }

  /// Phone: the device on top, a trunk down the middle, parts either side.
  Widget _narrow(double w) {
    final rows = (parts.length / 2).ceil();
    final colW = (w - 24) / 2;
    return Column(children: [
      _center(width: math.min(w, 320)),
      SizedBox(
        height: rows * tileH + rows * gap + 6,
        child: Stack(children: [
          Positioned.fill(child: CustomPaint(painter: _TrunkPainter(rows: rows, count: parts.length, colW: colW))),
          for (var i = 0; i < parts.length; i++)
            Positioned(
              left: i.isEven ? 0 : colW + 24,
              top: 18 + (i ~/ 2) * (tileH + gap),
              width: colW,
              child: _tile(parts[i]),
            ),
        ]),
      ),
    ]);
  }
}

class _HubPainter extends CustomPainter {
  _HubPainter({required this.left, required this.right, required this.colW, required this.height});
  final int left, right;
  final double colW, height;

  @override
  void paint(Canvas canvas, Size size) {
    final p = Paint()
      ..color = C.flow.withValues(alpha: 0.45)
      ..strokeWidth = 1.4
      ..style = PaintingStyle.stroke;
    final cy = height / 2;
    double y(int i) => i * (DeviceHub.tileH + DeviceHub.gap) + DeviceHub.tileH / 2;
    final lx = colW, rx = size.width - colW;
    final cl = colW + 48, cr = size.width - colW - 48;
    for (var i = 0; i < left; i++) {
      final path = Path()
        ..moveTo(lx, y(i))
        ..lineTo(lx + 24, y(i))
        ..lineTo(lx + 24, cy)
        ..lineTo(cl, cy);
      canvas.drawPath(path, p);
    }
    for (var i = 0; i < right; i++) {
      final path = Path()
        ..moveTo(rx, y(i))
        ..lineTo(rx - 24, y(i))
        ..lineTo(rx - 24, cy)
        ..lineTo(cr, cy);
      canvas.drawPath(path, p);
    }
  }

  @override
  bool shouldRepaint(_HubPainter o) => o.left != left || o.right != right || o.colW != colW || o.height != height;
}

class _TrunkPainter extends CustomPainter {
  _TrunkPainter({required this.rows, required this.count, required this.colW});
  final int rows, count;
  final double colW;

  @override
  void paint(Canvas canvas, Size size) {
    final p = Paint()
      ..color = C.flow.withValues(alpha: 0.45)
      ..strokeWidth = 1.4
      ..style = PaintingStyle.stroke;
    final x = colW + 12;
    double y(int row) => 18 + row * (DeviceHub.tileH + DeviceHub.gap) + DeviceHub.tileH / 2;
    canvas.drawLine(Offset(x, 0), Offset(x, y(rows - 1)), p);
    for (var i = 0; i < count; i++) {
      final yy = y(i ~/ 2);
      canvas.drawLine(Offset(x, yy), Offset(i.isEven ? colW : colW + 24, yy), p);
    }
  }

  @override
  bool shouldRepaint(_TrunkPainter o) => o.rows != rows || o.count != count || o.colW != colW;
}

/// One part on the hub: name, value and the live line.
class PartTile extends StatelessWidget {
  const PartTile(this.p, {super.key, required this.live, required this.onTap});
  final Part p;
  final List<Report> live;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final s = p.series.isEmpty ? null : p.series.first;
    final values = s == null ? const <double?>[] : [for (final r in live) s.live(r)];
    final border = p.level == Level.ok || p.level == Level.inactive ? C.line : C.of(p.level).withValues(alpha: 0.6);
    return Material(
      color: C.surface,
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.circular(12), side: BorderSide(color: border)),
      clipBehavior: Clip.antiAlias,
      child: InkWell(
        onTap: onTap,
        child: Padding(
          padding: const EdgeInsets.fromLTRB(12, 10, 12, 8),
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Row(children: [
              Icon(p.icon, size: 16, color: C.flow),
              const SizedBox(width: 6),
              Expanded(
                child: Text(p.title, maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(fontWeight: FontWeight.w600, fontSize: 13)),
              ),
              if (p.level == Level.warn || p.level == Level.fault) StatusDot(p.level, size: 8),
            ]),
            const SizedBox(height: 4),
            Text(p.value,
                maxLines: 1,
                overflow: TextOverflow.ellipsis,
                style: TextStyle(
                    fontSize: 18,
                    fontWeight: FontWeight.w700,
                    color: p.level == Level.inactive ? C.muted : C.text,
                    fontFeatures: const [FontFeature.tabularFigures()])),
            if (p.sub != null)
              Text(p.sub!, maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(color: C.muted, fontSize: 11)),
            const Spacer(),
            if (values.whereType<double>().length >= 2) Spark(values, max: s!.max, height: 18, color: C.of(p.level == Level.ok ? Level.ok : p.level).withValues(alpha: 0.9)),
          ]),
        ),
      ),
    );
  }
}

/// A chart with its numbers: highest at the top, the time span underneath.
class Chart extends StatelessWidget {
  const Chart({super.key, required this.label, required this.values, required this.series, required this.span, this.height = 110});
  final String label;
  final List<double?> values;
  final Series series;
  final String span;
  final double height;

  @override
  Widget build(BuildContext context) {
    final v = values.whereType<double>().toList();
    final last = v.isEmpty ? null : v.last;
    final top = series.max ?? (v.isEmpty ? 1.0 : math.max(v.reduce(math.max), 1.0));
    return Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
      Row(children: [
        Text(label, style: const TextStyle(color: C.muted, fontSize: 12)),
        const Spacer(),
        Text(series.fmt(last), style: const TextStyle(fontWeight: FontWeight.w700, fontFeatures: [FontFeature.tabularFigures()])),
      ]),
      const SizedBox(height: 6),
      Stack(children: [
        Spark(values, max: top, height: height),
        Positioned(right: 0, top: 0, child: Text(series.fmt(top), style: const TextStyle(color: C.muted, fontSize: 10))),
      ]),
      const SizedBox(height: 3),
      Row(children: [
        Text(span, style: const TextStyle(color: C.muted, fontSize: 10.5)),
        const Spacer(),
        const Text('now', style: TextStyle(color: C.muted, fontSize: 10.5)),
      ]),
      if (v.length >= 2) ...[
        const SizedBox(height: 4),
        Text('low ${series.fmt(v.reduce(math.min))}   ·   average ${series.fmt(v.reduce((a, b) => a + b) / v.length)}   ·   high ${series.fmt(v.reduce(math.max))}',
            style: const TextStyle(color: C.muted, fontSize: 11)),
      ],
    ]);
  }
}

/// One part, opened: what it is now, its live line, its history, its details,
/// and anything else on the same device that needs a look.
class PartPage extends StatefulWidget {
  const PartPage(this.state, this.deviceId, this.partId, {super.key});
  final AppState state;
  final String deviceId;
  final String partId;
  @override
  State<PartPage> createState() => _PartPageState();
}

class _PartPageState extends State<PartPage> {
  HistoryRange range = HistoryRange.day;
  HistoryRange? _loadedRange;
  int _seq = -1;
  List<Map<String, Object?>> history = const [];

  void _load(Report r) {
    if (r.seq == _seq && range == _loadedRange) return;
    // History files change at most every 15 minutes: no need to re-read on
    // every live update.
    if (range == _loadedRange && history.isNotEmpty && r.seq - _seq < 30) return;
    _seq = r.seq;
    _loadedRange = range;
    try {
      history = readHistoryRange(widget.state.dirOf(r.id), range, DateTime.now());
    } catch (_) {
      history = const [];
    }
  }

  @override
  Widget build(BuildContext context) {
    return ListenableBuilder(
      listenable: widget.state,
      builder: (context, _) {
        final s = widget.state.statusOf(widget.deviceId);
        if (s == null) return Scaffold(appBar: AppBar(), body: const Center(child: Text('This device has no readings yet.')));
        final r = s.report;
        final names = widget.state.names;
        final parts = partsOf(r, names, away: widget.state.away);
        final p = parts.where((x) => x.id == widget.partId).firstOrNull;
        if (p == null) return Scaffold(appBar: AppBar(), body: const Center(child: Text('This part is no longer reported.')));
        _load(r);
        final live = widget.state.live[r.id] ?? const <Report>[];
        final others = parts.where((x) => x.id != p.id && (x.level == Level.warn || x.level == Level.fault)).toList();
        final liveSpan = live.length >= 2 ? 'last ${ago(live.last.time.difference(live.first.time))}' : 'since you opened the app';
        return Scaffold(
          appBar: AppBar(
            titleSpacing: 0,
            title: Row(children: [
              Flexible(
                child: Text(r.name, overflow: TextOverflow.ellipsis, style: const TextStyle(color: C.muted, fontSize: 16)),
              ),
              const Icon(Icons.chevron_right, color: C.muted),
              Flexible(child: Text(p.title, overflow: TextOverflow.ellipsis)),
            ]),
          ),
          body: ListView(padding: const EdgeInsets.fromLTRB(14, 4, 14, 28), children: [
            Center(
              child: ConstrainedBox(
                constraints: const BoxConstraints(maxWidth: 860),
                child: Column(crossAxisAlignment: CrossAxisAlignment.stretch, children: [
                  Row(children: [
                    Icon(p.icon, color: C.flow, size: 30),
                    const SizedBox(width: 12),
                    Expanded(
                      child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
                        Text(p.value, style: const TextStyle(fontSize: 28, fontWeight: FontWeight.w700)),
                        if (p.sub != null) Text(p.sub!, style: const TextStyle(color: C.muted)),
                      ]),
                    ),
                    if (p.level == Level.warn || p.level == Level.fault) StatusChip(p.level, p.level == Level.fault ? 'Needs you' : 'Look at this'),
                  ]),
                  const SizedBox(height: 16),
                  if (p.series.isNotEmpty) ...[
                    Section(
                      'Live',
                      trailing: Text(r.live ? 'every ${r.nextSeconds} s' : 'every ${ago(Duration(seconds: r.nextSeconds))}',
                          style: TextStyle(color: r.live ? C.ok : C.muted, fontSize: 12)),
                      Column(children: [
                        for (final x in p.series) ...[
                          Chart(label: x.label, values: [for (final l in live) x.live(l)], series: x, span: liveSpan, height: 90),
                          if (x != p.series.last) const SizedBox(height: 14),
                        ],
                      ]),
                    ),
                    const SizedBox(height: 14),
                    Section(
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
                          ? const Text('Not enough history yet: readings move into history every 15 minutes, hourly and daily summaries after the first full hour and day.',
                              style: TextStyle(color: C.muted))
                          : Column(children: [
                              for (final x in p.series) ...[
                                Chart(
                                  label: x.label,
                                  values: [for (final l in history) chartValue(l, x.historyKey)],
                                  series: x,
                                  span: switch (range) { HistoryRange.day => '24 h ago', HistoryRange.week => '7 days ago', HistoryRange.year => 'a year ago' },
                                ),
                                if (x != p.series.last) const SizedBox(height: 14),
                              ],
                            ]),
                    ),
                    const SizedBox(height: 14),
                  ],
                  Section(
                    'Details',
                    Column(children: [
                      for (final f in p.facts)
                        Padding(
                          padding: const EdgeInsets.only(bottom: 8),
                          child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
                            SizedBox(width: 130, child: Text(f.$1, style: const TextStyle(color: C.muted, fontSize: 12.5))),
                            Expanded(child: SelectableText(f.$2, style: const TextStyle(fontSize: 12.5))),
                          ]),
                        ),
                    ]),
                  ),
                  if (others.isNotEmpty) ...[
                    const SizedBox(height: 14),
                    Section(
                      'Also on ${r.name}',
                      Column(children: [
                        for (final o in others)
                          ListTile(
                            dense: true,
                            contentPadding: EdgeInsets.zero,
                            leading: StatusDot(o.level),
                            title: Text('${o.title}: ${o.value}'),
                            subtitle: o.sub == null ? null : Text(o.sub!, style: const TextStyle(color: C.muted)),
                            trailing: const Icon(Icons.chevron_right),
                            onTap: () => Navigator.of(context).pushReplacement(
                                MaterialPageRoute<void>(builder: (_) => PartPage(widget.state, widget.deviceId, o.id))),
                          ),
                      ]),
                    ),
                  ],
                ]),
              ),
            ),
          ]),
        );
      },
    );
  }
}
