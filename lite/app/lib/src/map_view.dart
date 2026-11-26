import 'dart:math' as math;

import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';

import 'theme.dart';
import 'widgets.dart';

/// How two devices see each other, from both of their reports.
class Link {
  Link(this.a, this.b, this.ab, this.ba);
  final String a, b;
  final PeerView? ab, ba;

  bool _ok(PeerView? v) => v != null && v.reachable;

  Level get level {
    final x = _ok(ab), y = _ok(ba);
    if (ab == null && ba == null) return Level.inactive;
    if (x && y) return (ab?.tcp == 'refused' || ba?.tcp == 'refused') ? Level.warn : Level.ok;
    if (x || y) return Level.warn;
    if ((ab?.tcp == 'noip' || ab == null) && (ba?.tcp == 'noip' || ba == null)) return Level.inactive;
    return Level.fault;
  }

  int? get pingMs {
    final p = [ab?.pingMs, ba?.pingMs].whereType<int>();
    return p.isEmpty ? null : p.reduce(math.min);
  }

  String describe(String na, String nb) {
    String one(String from, String to, PeerView? v) => v == null
        ? '$from has not checked $to'
        : v.reachable
            ? '$from reaches $to${v.pingMs != null ? ' (${v.pingMs} ms)' : ''}${v.tcp == 'refused' ? ', Syncthing there is down' : ''}'
            : v.tcp == 'noip'
                ? '$from has no Tailscale address for $to'
                : '$from gets no answer from $to';
    return '${one(na, nb, ab)}. ${one(nb, na, ba)}.';
  }
}

List<Link> linksOf(List<DeviceStatus> devices) {
  final out = <Link>[];
  for (var i = 0; i < devices.length; i++) {
    for (var j = i + 1; j < devices.length; j++) {
      final a = devices[i].report, b = devices[j].report;
      final ab = a.peers[b.id], ba = b.peers[a.id];
      if (ab == null && ba == null) continue;
      out.add(Link(a.id, b.id, ab, ba));
    }
  }
  return out;
}

/// The network as a living schematic: each device a small card with its key
/// gauges, joined by cables. Dots travel along a cable in each direction a
/// device reaches the other, so a one-way link is visibly one-way. Colour
/// marks only problems. Tap a card to dive in, tap a cable to read it.
class NetworkMap extends StatefulWidget {
  const NetworkMap(this.devices, {super.key, required this.myId, required this.onOpen});
  final List<DeviceStatus> devices;
  final String myId;
  final void Function(String id) onOpen;

  @override
  State<NetworkMap> createState() => _NetworkMapState();
}

class _NetworkMapState extends State<NetworkMap> with TickerProviderStateMixin {
  Link? selected;
  String? hover;
  late final AnimationController flow = AnimationController(vsync: this, duration: const Duration(milliseconds: 2600))..repeat();
  late final AnimationController enter = AnimationController(vsync: this, duration: const Duration(milliseconds: 700))..forward();

  @override
  void dispose() {
    flow.dispose();
    enter.dispose();
    super.dispose();
  }

  static Size cardSize(Size area) => area.width < 460 ? const Size(150, 116) : const Size(196, 122);

  /// Card centres on a circle that fits the area, turned so that four
  /// devices sit on the corners of a square.
  static Map<String, Offset> layout(List<String> ids, Size area) {
    final c = Offset(area.width / 2, area.height / 2);
    if (ids.length == 1) return {ids.first: c};
    final card = cardSize(area);
    final turn = ids.length == 2 ? math.pi / 2 : ids.length == 4 ? math.pi / 4 : 0.0;
    final angles = [for (var i = 0; i < ids.length; i++) turn + 2 * math.pi * i / ids.length];
    final xs = angles.map((a) => math.sin(a).abs()).reduce(math.max);
    final ys = angles.map((a) => math.cos(a).abs()).reduce(math.max);
    final rx = (area.width - card.width) / 2 - 2, ry = (area.height - card.height) / 2 - 2;
    // An ellipse that uses the whole area, but never flatter than 2:1.
    var ex = math.max(30.0, rx / xs), ey = math.max(30.0, ry / ys);
    ex = math.min(ex, ey * 2);
    ey = math.min(ey, ex * 2);
    return {for (var i = 0; i < ids.length; i++) ids[i]: c + Offset(ex * math.sin(angles[i]), -ey * math.cos(angles[i]))};
  }

  @override
  Widget build(BuildContext context) {
    final devices = widget.devices;
    if (devices.isEmpty) {
      return const Center(child: Text('No devices yet.', style: TextStyle(color: C.muted)));
    }
    final names = {for (final d in devices) d.report.id: d.report.name};
    final links = linksOf(devices);
    return Column(children: [
      Expanded(
        child: ClipRRect(
          borderRadius: BorderRadius.circular(18),
          child: DecoratedBox(
            decoration: BoxDecoration(
              border: Border.all(color: C.line.withValues(alpha: 0.7)),
              borderRadius: BorderRadius.circular(18),
              color: const Color(0xFF15181D),
            ),
            child: Padding(padding: const EdgeInsets.all(12), child: _graph(devices, links)),
          ),
        ),
      ),
      const SizedBox(height: 10),
      AnimatedSwitcher(
        duration: const Duration(milliseconds: 220),
        child: selected != null
            ? _LinkDetail(
                key: ValueKey(selected),
                link: selected!,
                text: selected!.describe(names[selected!.a] ?? selected!.a, names[selected!.b] ?? selected!.b),
                onClose: () => setState(() => selected = null),
              )
            : const SizedBox(
                height: 40,
                child: Center(
                  child: Text('Dots show traffic each way  ·  tap a cable for details',
                      style: TextStyle(color: C.muted, fontSize: 11.5, letterSpacing: 0.1)),
                ),
              ),
      ),
    ]);
  }

  Widget _graph(List<DeviceStatus> devices, List<Link> links) {
    return LayoutBuilder(builder: (context, box) {
      final area = Size(box.maxWidth, box.maxHeight);
      final card = cardSize(area);
      final pos = layout([for (final d in devices) d.report.id], area);
      final rects = {for (final e in pos.entries) e.key: Rect.fromCenter(center: e.value, width: card.width, height: card.height)};
      final cables = {for (final l in links) l: _Cable.of(l, rects, Offset(area.width / 2, area.height / 2))};
      return Stack(children: [
        const Positioned.fill(child: CustomPaint(painter: _GridPainter())),
        Positioned.fill(
          child: GestureDetector(
            behavior: HitTestBehavior.opaque,
            onTapUp: (e) => setState(() => selected = _hit(cables, e.localPosition)),
            child: AnimatedBuilder(
              animation: Listenable.merge([flow, enter]),
              builder: (_, _) => CustomPaint(painter: _CablePainter(cables, selected, flow.value, enter.value)),
            ),
          ),
        ),
        for (var i = 0; i < devices.length; i++)
          Positioned.fromRect(
            rect: rects[devices[i].report.id]!,
            child: AnimatedBuilder(
              animation: enter,
              builder: (_, child) {
                final t = Curves.easeOutCubic.transform(((enter.value - i * 0.08) / 0.7).clamp(0.0, 1.0));
                return Opacity(opacity: t, child: Transform.translate(offset: Offset(0, 10 * (1 - t)), child: child));
              },
              child: MouseRegion(
                cursor: SystemMouseCursors.click,
                onEnter: (_) => setState(() => hover = devices[i].report.id),
                onExit: (_) => setState(() => hover = null),
                child: GestureDetector(
                  onTap: () => widget.onOpen(devices[i].report.id),
                  child: _Card(devices[i], isMe: devices[i].report.id == widget.myId, lifted: hover == devices[i].report.id),
                ),
              ),
            ),
          ),
      ]);
    });
  }

  Link? _hit(Map<Link, _Cable> cables, Offset p) {
    Link? best;
    var bestD = 16.0;
    for (final e in cables.entries) {
      final d = e.value.distanceTo(p);
      if (d < bestD) {
        bestD = d;
        best = e.key;
      }
    }
    return best;
  }
}

/// A cable: a gentle curve from one card's edge to the other's.
class _Cable {
  _Cable(this.path, this.mid);
  final Path path;
  final Offset mid;

  static _Cable of(Link l, Map<String, Rect> rects, Offset centre) {
    final ra = rects[l.a]!, rb = rects[l.b]!;
    final a = _edge(ra, rb.center), b = _edge(rb, ra.center);
    final v = b - a;
    final len = v.distance;
    var n = len == 0 ? Offset.zero : Offset(-v.dy, v.dx) / len;
    // Bow outward, away from the middle of the map.
    final m0 = (a + b) / 2 - centre;
    if (m0.dx * n.dx + m0.dy * n.dy < 0) n = -n;
    final bow = n * len * 0.10;
    final c1 = a + v * 0.3 + bow, c2 = a + v * 0.7 + bow;
    final path = Path()
      ..moveTo(a.dx, a.dy)
      ..cubicTo(c1.dx, c1.dy, c2.dx, c2.dy, b.dx, b.dy);
    // Bezier midpoint at t = 0.5.
    final mid = (a + c1 * 3 + c2 * 3 + b) / 8;
    return _Cable(path, mid);
  }

  /// Where the line from the rect's centre towards [to] leaves the rect.
  static Offset _edge(Rect r, Offset to) {
    final c = r.center, d = to - c;
    if (d == Offset.zero) return c;
    final sx = d.dx == 0 ? double.infinity : (r.width / 2 + 3) / d.dx.abs();
    final sy = d.dy == 0 ? double.infinity : (r.height / 2 + 3) / d.dy.abs();
    return c + d * math.min(sx, sy);
  }

  double distanceTo(Offset p) {
    var best = double.infinity;
    for (final m in path.computeMetrics()) {
      for (var t = 0.0; t <= m.length; t += 6) {
        final q = m.getTangentForOffset(t)!.position;
        best = math.min(best, (q - p).distance);
      }
    }
    return best;
  }
}

class _CablePainter extends CustomPainter {
  _CablePainter(this.cables, this.selected, this.t, this.enter);
  final Map<Link, _Cable> cables;
  final Link? selected;
  final double t, enter;

  static const _quiet = Color(0xFF3B4450);

  @override
  void paint(Canvas canvas, Size size) {
    final pills = <Rect>[];
    final order = cables.keys.toList()..sort((x, y) => x.level.index.compareTo(y.level.index));
    for (final l in order) {
      final cab = cables[l]!;
      final sel = identical(l, selected);
      final color = l.level == Level.ok ? _quiet : C.of(l.level);
      final m = cab.path.computeMetrics().first;
      final shown = m.extractPath(0, m.length * Curves.easeOutCubic.transform(enter));

      // A soft underlay gives the cable some depth.
      canvas.drawPath(
          shown,
          Paint()
            ..style = PaintingStyle.stroke
            ..strokeWidth = sel ? 9 : 6
            ..strokeCap = StrokeCap.round
            ..color = (l.level == Level.ok ? Colors.black : color).withValues(alpha: l.level == Level.ok ? 0.35 : 0.10));
      final line = Paint()
        ..style = PaintingStyle.stroke
        ..strokeWidth = sel ? 2.4 : 1.6
        ..strokeCap = StrokeCap.round
        ..color = sel && l.level == Level.ok ? const Color(0xFF5A6575) : color.withValues(alpha: l.level == Level.ok ? 1 : 0.8);
      if (l.level == Level.fault || l.level == Level.inactive) {
        for (var d = 0.0; d < m.length; d += 9) {
          canvas.drawPath(m.extractPath(d, math.min(d + 4, m.length)), line);
        }
      } else {
        canvas.drawPath(shown, line);
      }
      if (enter < 1) continue;

      // Traffic: two dots each way that a device reaches the other.
      final dot = l.level == Level.ok ? C.flow : C.of(l.level);
      void travel(bool forward) {
        for (var k = 0; k < 2; k++) {
          final p = (t + k / 2) % 1.0;
          final at = m.getTangentForOffset(m.length * (forward ? p : 1 - p))!.position;
          final fade = math.sin(p * math.pi); // appear and vanish at the ends
          canvas.drawCircle(at, 4.5, Paint()..color = dot.withValues(alpha: 0.18 * fade)..maskFilter = const MaskFilter.blur(BlurStyle.normal, 3));
          canvas.drawCircle(at, 1.9, Paint()..color = dot.withValues(alpha: 0.95 * fade));
        }
      }

      if (l.ab?.reachable == true) travel(true);
      if (l.ba?.reachable == true) travel(false);

      // Ping, on a small pill in the middle of the cable.
      final ms = l.pingMs;
      final label = ms != null ? '$ms ms' : (l.level == Level.fault ? 'no answer' : null);
      if (label != null && m.length > 70) {
        final tp = TextPainter(
          text: TextSpan(
              text: label,
              style: TextStyle(
                  fontFamily: 'Inter',
                  color: l.level == Level.fault ? C.fault : C.muted,
                  fontSize: 10.5,
                  fontWeight: FontWeight.w500,
                  fontFeatures: const [FontFeature.tabularFigures()])),
          textDirection: TextDirection.ltr,
        )..layout();
        // In the middle of the cable, or nearer one end if another pill is there.
        var box = Rect.zero;
        for (final f in const [0.5, 0.36, 0.64, 0.28, 0.72]) {
          final at = m.getTangentForOffset(m.length * f)!.position;
          box = Rect.fromCenter(center: at, width: tp.width + 14, height: tp.height + 6);
          if (!pills.any((p) => p.inflate(3).overlaps(box))) break;
        }
        pills.add(box);
        final r = RRect.fromRectAndRadius(box, const Radius.circular(20));
        canvas.drawRRect(r, Paint()..color = const Color(0xFF15181D));
        canvas.drawRRect(r, Paint()..style = PaintingStyle.stroke..strokeWidth = 1..color = color.withValues(alpha: l.level == Level.ok ? 1 : 0.5));
        tp.paint(canvas, box.center - Offset(tp.width / 2, tp.height / 2));
      }
    }
  }

  @override
  bool shouldRepaint(_CablePainter o) => true;
}

/// A faint blueprint dot grid.
class _GridPainter extends CustomPainter {
  const _GridPainter();
  @override
  void paint(Canvas canvas, Size size) {
    final p = Paint()..color = Colors.white.withValues(alpha: 0.05);
    const step = 22.0;
    for (var y = step / 2; y < size.height; y += step) {
      for (var x = step / 2; x < size.width; x += step) {
        canvas.drawCircle(Offset(x, y), 0.9, p);
      }
    }
  }

  @override
  bool shouldRepaint(_GridPainter o) => false;
}

/// One device: what it is, whether it is well, and its key gauges.
class _Card extends StatelessWidget {
  const _Card(this.s, {required this.isMe, required this.lifted});
  final DeviceStatus s;
  final bool isMe;
  final bool lifted;

  String get _what {
    final r = s.report;
    final os = r.os ?? '';
    if (r.kind == 'android') return kindIcon(r.kind, r.name) == Icons.tablet_android ? 'Tablet' : 'Phone';
    if (os.contains('Docker')) return 'Server · Docker';
    return r.kind == 'windows' ? 'Windows' : r.kind == 'linux' ? 'Linux' : r.kind;
  }

  @override
  Widget build(BuildContext context) {
    final r = s.report;
    final problem = s.level != Level.ok;
    final gauges = <(String, double?)>[
      if (r.cpuPct != null) ('CPU', r.cpuPct),
      ('MEM', pct(r.memUsed, r.memTotal)),
      if (r.battery?.pct != null) ('BAT', r.battery!.pct),
      if (r.battery?.pct == null || r.cpuPct == null) ('DISK', pct(r.diskUsed, r.diskTotal)),
    ].take(3).toList();
    return AnimatedContainer(
      duration: const Duration(milliseconds: 160),
      curve: Curves.easeOut,
      transform: Matrix4.translationValues(0, lifted ? -3 : 0, 0),
      padding: const EdgeInsets.fromLTRB(12, 10, 12, 10),
      decoration: BoxDecoration(
        borderRadius: BorderRadius.circular(14),
        gradient: const LinearGradient(begin: Alignment.topCenter, end: Alignment.bottomCenter, colors: [Color(0xFF232830), Color(0xFF1B1F25)]),
        border: Border.all(
            color: problem ? C.of(s.level).withValues(alpha: 0.55) : isMe ? C.flow.withValues(alpha: 0.45) : const Color(0xFF323944)),
        boxShadow: [
          BoxShadow(color: Colors.black.withValues(alpha: lifted ? 0.55 : 0.4), blurRadius: lifted ? 26 : 18, offset: Offset(0, lifted ? 12 : 8)),
        ],
      ),
      child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
        Row(children: [
          Container(
            width: 28,
            height: 28,
            decoration: BoxDecoration(color: const Color(0xFF14171B), borderRadius: BorderRadius.circular(8), border: Border.all(color: const Color(0xFF2C323B))),
            child: Icon(kindIcon(r.kind, r.name), size: 15, color: s.fresh ? C.text : C.muted),
          ),
          const SizedBox(width: 9),
          Expanded(
            child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
              Text(r.name, maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 13, fontWeight: FontWeight.w600, height: 1.2)),
              Text(problem ? s.title : (isMe ? '$_what · this device' : _what),
                  maxLines: 1,
                  overflow: TextOverflow.ellipsis,
                  style: TextStyle(fontSize: 10.5, color: problem ? C.of(s.level) : C.muted, height: 1.3)),
            ]),
          ),
          _Pulse(s.level, live: s.report.live),
        ]),
        const Spacer(),
        Row(mainAxisAlignment: MainAxisAlignment.spaceAround, children: [for (final g in gauges) _Ring(g.$1, g.$2)]),
      ]),
    );
  }
}

/// A status dot that breathes while the device is reporting live.
class _Pulse extends StatefulWidget {
  const _Pulse(this.level, {required this.live});
  final Level level;
  final bool live;
  @override
  State<_Pulse> createState() => _PulseState();
}

class _PulseState extends State<_Pulse> with SingleTickerProviderStateMixin {
  late final AnimationController c = AnimationController(vsync: this, duration: const Duration(milliseconds: 1800))..repeat();

  @override
  void dispose() {
    c.dispose();
    super.dispose();
  }

  @override
  Widget build(BuildContext context) {
    final color = C.of(widget.level);
    return SizedBox(
      width: 16,
      height: 16,
      child: AnimatedBuilder(
        animation: c,
        builder: (_, _) => CustomPaint(
          painter: _PulsePainter(color, widget.live ? c.value : null),
        ),
      ),
    );
  }
}

class _PulsePainter extends CustomPainter {
  _PulsePainter(this.color, this.t);
  final Color color;
  final double? t;
  @override
  void paint(Canvas canvas, Size size) {
    final c = size.center(Offset.zero);
    if (t != null) {
      canvas.drawCircle(c, 3 + 5 * t!, Paint()..color = color.withValues(alpha: 0.35 * (1 - t!)));
    }
    canvas.drawCircle(c, 3.2, Paint()..color = color);
  }

  @override
  bool shouldRepaint(_PulsePainter o) => o.t != t || o.color != color;
}

/// A small ring gauge with its value inside and a label under it.
class _Ring extends StatelessWidget {
  const _Ring(this.label, this.value);
  final String label;
  final double? value;

  /// Full is bad for most gauges, empty is bad for a battery.
  Level get level {
    final v = value;
    if (v == null) return Level.inactive;
    if (label == 'BAT') return v < 10 ? Level.fault : v < 20 ? Level.warn : Level.ok;
    return v >= 95 ? Level.fault : v >= 85 ? Level.warn : Level.ok;
  }

  @override
  Widget build(BuildContext context) {
    final v = value;
    final l = level;
    return Column(mainAxisSize: MainAxisSize.min, children: [
      SizedBox(
        width: 30,
        height: 30,
        child: CustomPaint(
          painter: _RingPainter(v, l == Level.ok ? const Color(0xFFC9D1DC) : C.of(l)),
          child: Center(
            child: Text(v == null ? '–' : '${v.round()}',
                style: const TextStyle(fontSize: 10, fontWeight: FontWeight.w600, fontFeatures: [FontFeature.tabularFigures()])),
          ),
        ),
      ),
      const SizedBox(height: 3),
      Text(label, style: const TextStyle(fontSize: 8.5, color: C.muted, fontWeight: FontWeight.w600, letterSpacing: 0.8)),
    ]);
  }
}

class _RingPainter extends CustomPainter {
  _RingPainter(this.v, this.color);
  final double? v;
  final Color color;
  @override
  void paint(Canvas canvas, Size size) {
    final r = Rect.fromCircle(center: size.center(Offset.zero), radius: size.width / 2 - 2);
    final base = Paint()
      ..style = PaintingStyle.stroke
      ..strokeWidth = 2.6
      ..color = const Color(0xFF2E353F);
    canvas.drawArc(r, 0, 2 * math.pi, false, base);
    if (v == null) return;
    canvas.drawArc(r, -math.pi / 2, 2 * math.pi * (v!.clamp(0, 100) / 100), false, base..color = color..strokeCap = StrokeCap.round);
  }

  @override
  bool shouldRepaint(_RingPainter o) => o.v != v || o.color != color;
}

class _LinkDetail extends StatelessWidget {
  const _LinkDetail({super.key, required this.link, required this.text, required this.onClose});
  final Link link;
  final String text;
  final VoidCallback onClose;

  @override
  Widget build(BuildContext context) => Container(
        padding: const EdgeInsets.fromLTRB(14, 8, 4, 8),
        decoration: BoxDecoration(
          color: C.surface,
          borderRadius: BorderRadius.circular(12),
          border: Border.all(color: link.level == Level.ok ? C.line : C.of(link.level).withValues(alpha: 0.5)),
        ),
        child: Row(children: [
          StatusDot(link.level, size: 8),
          const SizedBox(width: 12),
          Expanded(child: Text(text, style: const TextStyle(fontSize: 12.5, height: 1.35))),
          IconButton(onPressed: onClose, icon: const Icon(Icons.close, size: 16, color: C.muted)),
        ]),
      );
}
