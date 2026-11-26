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

/// The network as a graph: every device a node, every pair of devices that
/// checked each other a line coloured by how well they reach each other.
/// Tap a device to dive into it, tap a line to read what each side sees.
class NetworkMap extends StatefulWidget {
  const NetworkMap(this.devices, {super.key, required this.myId, required this.onOpen});
  final List<DeviceStatus> devices;
  final String myId;
  final void Function(String id) onOpen;

  @override
  State<NetworkMap> createState() => _NetworkMapState();
}

class _NetworkMapState extends State<NetworkMap> {
  Link? selected;

  static Map<String, Offset> layout(List<String> ids, Size size) {
    final c = Offset(size.width / 2, size.height / 2);
    if (ids.length == 1) return {ids.first: c - const Offset(0, 14)};
    final rx = math.max(60.0, size.width / 2 - 80), ry = math.max(60.0, size.height / 2 - 60);
    final c2 = c - const Offset(0, 20); // room for the labels under the bottom node
    return {
      for (var i = 0; i < ids.length; i++)
        ids[i]: c2 + Offset(rx * math.sin(2 * math.pi * i / ids.length), -ry * math.cos(2 * math.pi * i / ids.length)),
    };
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
      Expanded(child: _graph(devices, links)),
      const SizedBox(height: 8),
      if (selected != null)
        Card(
          child: Padding(
            padding: const EdgeInsets.fromLTRB(14, 10, 6, 10),
            child: Row(children: [
              StatusDot(selected!.level, size: 9),
              const SizedBox(width: 10),
              Expanded(
                child: Text(selected!.describe(names[selected!.a] ?? selected!.a, names[selected!.b] ?? selected!.b),
                    style: const TextStyle(fontSize: 12.5, height: 1.35)),
              ),
              IconButton(onPressed: () => setState(() => selected = null), icon: const Icon(Icons.close, size: 18)),
            ]),
          ),
        )
      else
        const _Legend(),
    ]);
  }

  Widget _graph(List<DeviceStatus> devices, List<Link> links) {
    return LayoutBuilder(builder: (context, box) {
      final size = Size(box.maxWidth, box.maxHeight);
      final pos = layout([for (final d in devices) d.report.id], size);
      return Stack(children: [
        Positioned.fill(
          child: GestureDetector(
            behavior: HitTestBehavior.opaque,
            onTapUp: (e) => setState(() => selected = _hitLink(links, pos, e.localPosition)),
            child: CustomPaint(painter: _LinksPainter(links, pos, selected)),
          ),
        ),
        for (final d in devices)
          Positioned(
            left: pos[d.report.id]!.dx - 70,
            top: pos[d.report.id]!.dy - 34,
            width: 140,
            child: _Node(d, isMe: d.report.id == widget.myId, onTap: () => widget.onOpen(d.report.id)),
          ),
      ]);
    });
  }

  Link? _hitLink(List<Link> links, Map<String, Offset> pos, Offset p) {
    Link? best;
    var bestD = 18.0;
    for (final l in links) {
      final a = pos[l.a], b = pos[l.b];
      if (a == null || b == null) continue;
      final d = _distToSegment(p, a, b);
      if (d < bestD) {
        bestD = d;
        best = l;
      }
    }
    return best;
  }

  static double _distToSegment(Offset p, Offset a, Offset b) {
    final ab = b - a;
    final len2 = ab.dx * ab.dx + ab.dy * ab.dy;
    if (len2 == 0) return (p - a).distance;
    final t = (((p - a).dx * ab.dx + (p - a).dy * ab.dy) / len2).clamp(0.0, 1.0);
    return (p - (a + ab * t)).distance;
  }
}

class _Node extends StatelessWidget {
  const _Node(this.s, {required this.isMe, required this.onTap});
  final DeviceStatus s;
  final bool isMe;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final r = s.report;
    final color = C.of(s.level);
    final key = r.battery?.pct != null ? '${r.battery!.pct!.round()}% battery' : r.cpuPct != null ? 'CPU ${r.cpuPct!.round()}%' : null;
    return GestureDetector(
      onTap: onTap,
      child: Column(mainAxisSize: MainAxisSize.min, children: [
        Container(
          width: 56,
          height: 56,
          decoration: BoxDecoration(
            color: C.surface,
            shape: BoxShape.circle,
            border: Border.all(color: color, width: isMe ? 3 : 2),
            boxShadow: [BoxShadow(color: color.withValues(alpha: 0.25), blurRadius: 14)],
          ),
          child: Icon(kindIcon(r.kind, r.name), color: s.fresh ? C.text : C.muted, size: 26),
        ),
        const SizedBox(height: 4),
        Container(
          padding: const EdgeInsets.symmetric(horizontal: 6, vertical: 2),
          decoration: BoxDecoration(color: C.bg.withValues(alpha: 0.9), borderRadius: BorderRadius.circular(6)),
          child: Column(mainAxisSize: MainAxisSize.min, children: [
            Text(r.name, maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(fontWeight: FontWeight.w600, fontSize: 13)),
            Text(s.title, maxLines: 1, overflow: TextOverflow.ellipsis, style: TextStyle(color: color, fontSize: 11.5)),
            if (key != null) Text(key, style: const TextStyle(color: C.muted, fontSize: 11)),
          ]),
        ),
      ]),
    );
  }
}

class _LinksPainter extends CustomPainter {
  _LinksPainter(this.links, this.pos, this.selected);
  final List<Link> links;
  final Map<String, Offset> pos;
  final Link? selected;

  @override
  void paint(Canvas canvas, Size size) {
    for (final l in links) {
      final a = pos[l.a], b = pos[l.b];
      if (a == null || b == null) continue;
      final color = C.of(l.level);
      final sel = identical(l, selected);
      final paint = Paint()
        ..color = color.withValues(alpha: sel ? 1 : 0.75)
        ..strokeWidth = sel ? 3.5 : 2
        ..style = PaintingStyle.stroke;
      if (l.level == Level.inactive || l.level == Level.fault) {
        _dashed(canvas, a, b, paint);
      } else {
        canvas.drawLine(a, b, paint);
      }
      final ms = l.pingMs;
      if (ms != null) {
        final mid = Offset.lerp(a, b, 0.5)!;
        final tp = TextPainter(
          text: TextSpan(text: '$ms ms', style: const TextStyle(color: C.muted, fontSize: 11)),
          textDirection: TextDirection.ltr,
        )..layout();
        final r = Rect.fromCenter(center: mid, width: tp.width + 10, height: tp.height + 4);
        canvas.drawRRect(RRect.fromRectAndRadius(r, const Radius.circular(6)), Paint()..color = C.bg);
        tp.paint(canvas, mid - Offset(tp.width / 2, tp.height / 2));
      }
    }
  }

  void _dashed(Canvas c, Offset a, Offset b, Paint p) {
    final d = (b - a).distance;
    if (d == 0) return;
    final dir = (b - a) / d;
    for (var t = 0.0; t < d; t += 12) {
      c.drawLine(a + dir * t, a + dir * math.min(t + 6, d), p);
    }
  }

  @override
  bool shouldRepaint(_LinksPainter o) => o.links != links || o.pos != pos || o.selected != selected;
}

class _Legend extends StatelessWidget {
  const _Legend();
  @override
  Widget build(BuildContext context) {
    Widget item(Level l, String t) => Row(mainAxisSize: MainAxisSize.min, children: [
          Container(width: 16, height: 3, color: C.of(l)),
          const SizedBox(width: 6),
          Text(t, style: const TextStyle(color: C.muted, fontSize: 11.5)),
        ]);
    return Wrap(spacing: 16, runSpacing: 6, alignment: WrapAlignment.center, children: [
      item(Level.ok, 'reach each other'),
      item(Level.warn, 'one way, or Syncthing down'),
      item(Level.fault, 'no answer'),
      item(Level.inactive, 'not checked'),
    ]);
  }
}
