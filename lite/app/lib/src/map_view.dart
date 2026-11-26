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

  static const _node = 34.0;

  /// Devices on a circle that fits the shorter side (never stretched across
  /// a wide screen), turned so that four devices sit on the corners of a
  /// square: then every line runs inward and no label sits on a line.
  static Map<String, Offset> layout(List<String> ids, Size size) {
    final c = Offset(size.width / 2, size.height / 2);
    if (ids.length == 1) return {ids.first: c};
    final turn = ids.length == 2 ? math.pi / 2 : ids.length == 4 ? math.pi / 4 : 0.0;
    // Room for the labels: beside a device on the left or right, else above
    // or below it (130 wide, 34 high, see _node2).
    final sides = [for (var i = 0; i < ids.length; i++) math.sin(turn + 2 * math.pi * i / ids.length).abs() > 0.8].contains(true);
    final rx = (size.width - (sides ? 2 * (_node / 2 + 6 + 130) : 130)) / 2;
    final ry = (size.height - 2 * (_node / 2 + 6 + 34)) / 2;
    // The circle may be larger than that where no device sits at its widest
    // or tallest point (four devices on a square's corners).
    final angles = [for (var i = 0; i < ids.length; i++) turn + 2 * math.pi * i / ids.length];
    final xs = angles.map((a) => math.sin(a).abs()).reduce(math.max);
    final ys = angles.map((a) => math.cos(a).abs()).reduce(math.max);
    final r = math.max(40.0, math.min(rx / xs, ry / ys));
    return {
      for (var i = 0; i < ids.length; i++)
        ids[i]: c + Offset(r * math.sin(turn + 2 * math.pi * i / ids.length), -r * math.cos(turn + 2 * math.pi * i / ids.length)),
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
      const SizedBox(height: 4),
      SizedBox(
        height: 44,
        child: selected != null
            ? Row(children: [
                StatusDot(selected!.level, size: 8),
                const SizedBox(width: 10),
                Expanded(
                  child: Text(selected!.describe(names[selected!.a] ?? selected!.a, names[selected!.b] ?? selected!.b),
                      maxLines: 2, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 12.5, height: 1.3)),
                ),
                IconButton(onPressed: () => setState(() => selected = null), icon: const Icon(Icons.close, size: 16)),
              ])
            : Center(child: _Legend(links)),
      ),
    ]);
  }

  Widget _graph(List<DeviceStatus> devices, List<Link> links) {
    return LayoutBuilder(builder: (context, box) {
      final size = Size(box.maxWidth, box.maxHeight);
      final pos = layout([for (final d in devices) d.report.id], size);
      final c = Offset(size.width / 2, size.height / 2);
      return Stack(clipBehavior: Clip.none, children: [
        Positioned.fill(
          child: GestureDetector(
            behavior: HitTestBehavior.opaque,
            onTapUp: (e) => setState(() => selected = _hitLink(links, pos, e.localPosition)),
            child: CustomPaint(painter: _LinksPainter(links, pos, selected, _node / 2 + 4)),
          ),
        ),
        for (final d in devices) ..._node2(d, pos[d.report.id]!, c),
      ]);
    });
  }

  /// The device's circle, and its name on the outer side of it, where no
  /// line runs.
  List<Widget> _node2(DeviceStatus d, Offset p, Offset c) {
    final v = p - c;
    final u = v.distance == 0 ? const Offset(0, 1) : v / v.distance;
    const w = 130.0, h = 34.0, gap = 6.0, r = _node / 2;
    void open() => widget.onOpen(d.report.id);
    final label = _Label(d);
    final Widget placed;
    if (u.dx.abs() > 0.8) {
      final right = u.dx > 0;
      placed = Positioned(
        left: right ? p.dx + r + gap : p.dx - r - gap - w,
        top: p.dy - h / 2,
        width: w,
        height: h,
        child: Align(
          alignment: right ? Alignment.centerLeft : Alignment.centerRight,
          child: label.aligned(right ? CrossAxisAlignment.start : CrossAxisAlignment.end),
        ),
      );
    } else {
      final below = u.dy > 0;
      placed = Positioned(
        left: p.dx - w / 2,
        top: below ? p.dy + r + gap : p.dy - r - gap - h,
        width: w,
        height: h,
        child: Align(
          alignment: below ? Alignment.topCenter : Alignment.bottomCenter,
          child: label.aligned(CrossAxisAlignment.center),
        ),
      );
    }
    return [
      Positioned(left: p.dx - r, top: p.dy - r, child: GestureDetector(onTap: open, child: _Dot(d, isMe: d.report.id == widget.myId))),
      Positioned(
        left: (placed as Positioned).left,
        top: placed.top,
        width: placed.width,
        height: placed.height,
        child: GestureDetector(onTap: open, behavior: HitTestBehavior.translucent, child: placed.child),
      ),
    ];
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

/// A plain circle with the device's icon. Grey when all is well; the ring
/// takes a colour only when something needs a look. This device is filled.
class _Dot extends StatelessWidget {
  const _Dot(this.s, {required this.isMe});
  final DeviceStatus s;
  final bool isMe;

  @override
  Widget build(BuildContext context) {
    final ring = s.level == Level.ok ? C.muted.withValues(alpha: 0.6) : C.of(s.level);
    return Container(
      width: _NetworkMapState._node,
      height: _NetworkMapState._node,
      decoration: BoxDecoration(
        color: isMe ? C.raised : C.bg,
        shape: BoxShape.circle,
        border: Border.all(color: ring, width: 1.5),
      ),
      child: Icon(kindIcon(s.report.kind, s.report.name), color: s.fresh ? C.text : C.muted, size: 17),
    );
  }
}

/// Name, and one short line only when something is wrong.
class _Label {
  _Label(this.s);
  final DeviceStatus s;

  Widget aligned(CrossAxisAlignment a) {
    final ta = a == CrossAxisAlignment.start ? TextAlign.left : a == CrossAxisAlignment.end ? TextAlign.right : TextAlign.center;
    return Column(mainAxisSize: MainAxisSize.min, crossAxisAlignment: a, children: [
      Text(s.report.name, maxLines: 1, overflow: TextOverflow.ellipsis, textAlign: ta,
          style: const TextStyle(fontWeight: FontWeight.w600, fontSize: 12.5)),
      if (s.level != Level.ok)
        Text(s.title, maxLines: 1, overflow: TextOverflow.ellipsis, textAlign: ta, style: TextStyle(color: C.of(s.level), fontSize: 11)),
    ]);
  }
}

class _LinksPainter extends CustomPainter {
  _LinksPainter(this.links, this.pos, this.selected, this.inset);
  final List<Link> links;
  final Map<String, Offset> pos;
  final Link? selected;

  /// Lines stop this far from a device's centre, short of its circle.
  final double inset;

  @override
  void paint(Canvas canvas, Size size) {
    // Quiet lines first, problems on top.
    final order = [...links]..sort((x, y) => x.level.index.compareTo(y.level.index));
    for (final l in order) {
      var a = pos[l.a], b = pos[l.b];
      if (a == null || b == null) continue;
      final d = (b - a).distance;
      if (d <= inset * 2) continue;
      final dir = (b - a) / d;
      a = a + dir * inset;
      b = b - dir * inset;
      final sel = identical(l, selected);
      final color = l.level == Level.ok ? C.muted.withValues(alpha: sel ? 0.9 : 0.45) : C.of(l.level).withValues(alpha: sel ? 1 : 0.85);
      final paint = Paint()
        ..color = color
        ..strokeWidth = sel ? 2.5 : 1.5
        ..style = PaintingStyle.stroke;
      if (l.level == Level.inactive || l.level == Level.fault) {
        _dashed(canvas, a, b, paint);
      } else {
        canvas.drawLine(a, b, paint);
      }
    }
  }

  void _dashed(Canvas c, Offset a, Offset b, Paint p) {
    final d = (b - a).distance;
    if (d == 0) return;
    final dir = (b - a) / d;
    for (var t = 0.0; t < d; t += 10) {
      c.drawLine(a + dir * t, a + dir * math.min(t + 5, d), p);
    }
  }

  @override
  bool shouldRepaint(_LinksPainter o) => o.links != links || o.pos != pos || o.selected != selected;
}

/// Only the kinds of line on screen, in a few words.
class _Legend extends StatelessWidget {
  const _Legend(this.links);
  final List<Link> links;

  @override
  Widget build(BuildContext context) {
    final present = links.map((l) => l.level).toSet();
    Widget item(Level l, String t, {bool dashed = false}) => Row(mainAxisSize: MainAxisSize.min, children: [
          SizedBox(
            width: 16,
            height: 2,
            child: dashed
                ? Row(children: [Container(width: 5, color: C.of(l)), const SizedBox(width: 5), Container(width: 5, color: C.of(l))])
                : Container(color: l == Level.ok ? C.muted.withValues(alpha: 0.6) : C.of(l)),
          ),
          const SizedBox(width: 6),
          Text(t, style: const TextStyle(color: C.muted, fontSize: 11.5)),
        ]);
    return Wrap(spacing: 14, runSpacing: 4, alignment: WrapAlignment.center, children: [
      if (present.contains(Level.ok)) item(Level.ok, 'both ways'),
      if (present.contains(Level.warn)) item(Level.warn, 'one way'),
      if (present.contains(Level.fault)) item(Level.fault, 'no answer', dashed: true),
      if (present.contains(Level.inactive)) item(Level.inactive, 'not checked', dashed: true),
      const Text('tap a line for details', style: TextStyle(color: C.muted, fontSize: 11.5)),
    ]);
  }
}
