import 'dart:math' as math;

import 'package:flutter/gestures.dart';
import 'package:flutter/scheduler.dart';
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

/// Which family a device belongs to on the map. Families gather together
/// and, once the map is busy, can fold into one node.
String familyOf(Report r) {
  if (r.kind == 'android') return 'Phones and tablets';
  if ((r.os ?? '').contains('Docker') || RegExp(r'server|nas|pi-|docker', caseSensitive: false).hasMatch(r.name)) return 'Servers';
  return 'Computers';
}

/// The network as a living schematic that arranges itself: devices push
/// apart, families pull together, and the view follows as devices come and
/// go. Pan and zoom; with many devices each family can fold into one node.
/// Dots travel along a cable in each direction a device reaches the other.
class NetworkMap extends StatefulWidget {
  const NetworkMap(this.devices, {super.key, required this.myId, required this.onOpen, this.things = const []});
  final List<DeviceStatus> devices;

  /// Attached things (UPS, audio interface). Each hangs under the device its
  /// file names as parent, and folds into it.
  final List<DeviceStatus> things;
  final String myId;
  final void Function(String id) onOpen;

  @override
  State<NetworkMap> createState() => _NetworkMapState();
}

/// A node's place in the map's own coordinates, and how it moves.
class _Body {
  _Body(this.p);
  Offset p;
  Offset v = Offset.zero;
  double appear = 0; // 0 to 1 as it arrives
}

/// What is drawn as one node: a device, or a folded family.
class _Node {
  _Node(this.id, this.s, this.family, {this.members = const [], this.parent});
  final String id;
  final DeviceStatus s;
  final String family;
  final List<DeviceStatus> members; // non-empty for a folded family
  final String? parent; // set for an attached thing
  bool get folded => members.isNotEmpty;
  bool get thing => parent != null;
}

/// A cable between two nodes.
class _Edge {
  _Edge(this.a, this.b, this.level, this.fwd, this.back, this.ping, this.link, this.count);
  final String a, b;
  final Level level;
  final bool fwd, back;
  final int? ping;
  final Link? link; // null when it stands for several links
  final int count;
}

class _NetworkMapState extends State<NetworkMap> with TickerProviderStateMixin {
  final bodies = <String, _Body>{};
  final folded = <String>{};
  late final Ticker physics = createTicker(_step);
  late final AnimationController flow = AnimationController(vsync: this, duration: const Duration(milliseconds: 2600))..repeat();

  // Camera: screen = centre + pan + world * zoom.
  Offset pan = Offset.zero;
  double zoom = 1;
  bool userMoved = false;
  Size area = Size.zero;

  String? focus; // node whose cables are shown in full
  _Edge? selected;
  int _calm = 0;

  List<_Node> nodes = [];
  List<_Edge> edges = [];

  /// Device id -> its attached things, as declared in their files.
  Map<String, List<DeviceStatus>> attached = {};

  /// Devices whose things are shown (quiet map) or hidden (busy map)
  /// against the default.
  final toggled = <String>{};
  bool isOpen(String host) => busy ? toggled.contains(host) : !toggled.contains(host);

  static const thingSize = Size(196, 44);
  Size sizeOf(_Node n) => n.thing ? thingSize : nodeSize;

  /// Where the junction under [host] sits, in map coordinates.
  Offset junction(String host) => bodies[host]!.p + Offset(0, nodeSize.height / 2 + 26);

  void _toggleThings(String host) {
    setState(() {
      toggled.contains(host) ? toggled.remove(host) : toggled.add(host);
      _rebuild();
    });
  }

  /// Many devices: compact nodes, families, quiet cables.
  bool get busy => widget.devices.length > 7;
  Size get nodeSize => busy ? const Size(168, 46) : (area.width < 460 ? const Size(166, 118) : const Size(190, 120));

  @override
  void initState() {
    super.initState();
    _rebuild();
  }

  @override
  void didUpdateWidget(NetworkMap old) {
    super.didUpdateWidget(old);
    _rebuild();
  }

  @override
  void dispose() {
    physics.dispose();
    flow.dispose();
    super.dispose();
  }

  // ---- model --------------------------------------------------------------

  void _rebuild() {
    final devices = widget.devices;
    final byFamily = <String, List<DeviceStatus>>{};
    for (final d in devices) {
      byFamily.putIfAbsent(familyOf(d.report), () => []).add(d);
    }
    folded.removeWhere((f) => (byFamily[f]?.length ?? 0) < 2);
    final owner = <String, String>{}; // device id -> node id
    final out = <_Node>[];
    for (final d in devices) {
      final f = familyOf(d.report);
      if (folded.contains(f)) {
        owner[d.report.id] = 'family:$f';
        continue;
      }
      owner[d.report.id] = d.report.id;
      out.add(_Node(d.report.id, d, f));
    }
    for (final f in folded) {
      final m = byFamily[f]!;
      final worst = m.map((d) => d.level).reduce((a, b) => a.index >= b.index ? a : b);
      final bad = m.where((d) => d.level != Level.ok).length;
      final rep = Report(id: 'family:$f', name: f, kind: m.first.report.kind, time: DateTime.now().toUtc());
      out.add(_Node('family:$f', DeviceStatus(rep, worst, bad == 0 ? 'All ${m.length} well' : '$bad of ${m.length} need a look', '', fresh: true, age: Duration.zero), f,
          members: m));
    }

    // Cables, folded families merged into one cable per pair of nodes.
    final merged = <String, List<Link>>{};
    for (final l in linksOf(devices)) {
      final a = owner[l.a]!, b = owner[l.b]!;
      if (a == b) continue;
      merged.putIfAbsent(a.compareTo(b) < 0 ? '$a\n$b' : '$b\n$a', () => []).add(l);
    }
    edges = [
      for (final e in merged.entries)
        if (e.value.length == 1)
          _Edge(owner[e.value.first.a]!, owner[e.value.first.b]!, e.value.first.level, e.value.first.ab?.reachable == true,
              e.value.first.ba?.reachable == true, e.value.first.pingMs, e.value.first, 1)
        else
          _Edge(e.key.split('\n')[0], e.key.split('\n')[1], e.value.map((l) => l.level).reduce((a, b) => a.index >= b.index ? a : b),
              e.value.any((l) => l.ab?.reachable == true || l.ba?.reachable == true), e.value.any((l) => l.ab?.reachable == true || l.ba?.reachable == true),
              null, null, e.value.length),
    ];

    // Attached things hang under their device, unless it is folded away.
    attached = {};
    for (final t in widget.things) {
      final parent = t.report.node?.parent;
      if (parent == null || owner[parent] != parent) continue;
      attached.putIfAbsent(parent, () => []).add(t);
    }
    for (final e in attached.entries) {
      if (!isOpen(e.key)) continue;
      final host = out.firstWhere((n) => n.id == e.key);
      for (final t in e.value) {
        out.add(_Node(t.report.id, t, host.family, parent: e.key));
      }
    }

    // Bodies: keep the ones we have, start new ones beside their family.
    final ids = {for (final n in out) n.id};
    bodies.removeWhere((id, _) => !ids.contains(id));
    for (final n in out) {
      if (bodies.containsKey(n.id)) continue;
      Offset start;
      final kin = [
        for (final m in out)
          if (m.family == n.family && !m.thing && bodies.containsKey(m.id)) bodies[m.id]!.p,
        if (n.folded)
          for (final d in n.members)
            if (_lastPos[d.report.id] != null) _lastPos[d.report.id]!,
      ];
      final h = n.id.hashCode;
      final jitter = Offset(((h & 0xff) - 128) / 4, (((h >> 8) & 0xff) - 128) / 4);
      if (kin.isNotEmpty) {
        start = kin.reduce((a, b) => a + b) / kin.length.toDouble() + jitter;
      } else if (_lastPos[n.id] != null) {
        start = _lastPos[n.id]!;
      } else {
        final a = (h % 360) * math.pi / 180;
        start = Offset(math.cos(a), math.sin(a)) * (60.0 + 12 * out.length);
      }
      if (n.thing && bodies[n.parent] != null) start = bodies[n.parent]!.p + jitter / 4;
      bodies[n.id] = _Body(start)..appear = bodies.isEmpty ? 1 : 0;
    }
    if (bodies.values.every((b) => b.appear == 0)) {
      for (final b in bodies.values) {
        b.appear = 1;
      }
    }
    final changed = nodes.length != out.length || !nodes.every((n) => ids.contains(n.id));
    nodes = out;
    if (focus != null && !ids.contains(focus)) focus = null;
    if (changed) _wake();
  }

  /// Where folded members last were, so unfolding starts from there.
  final _lastPos = <String, Offset>{};

  void _wake() {
    _calm = 0;
    if (!physics.isActive) physics.start();
  }

  void _toggleFamily(String f) {
    setState(() {
      if (folded.contains(f)) {
        folded.remove(f);
      } else {
        for (final n in nodes) {
          if (n.family == f) _lastPos[n.id] = bodies[n.id]!.p;
        }
        folded.add(f);
      }
      focus = null;
      selected = null;
      _rebuild();
    });
  }

  // ---- physics --------------------------------------------------------------

  void _step(Duration _) {
    final list = nodes;
    final sz = nodeSize;
    // Room each node wants around it, as an ellipse matching its shape.
    final gap = busy ? 46.0 : 64.0;
    final room = Offset(sz.width + gap, sz.height + gap);
    final reachR = room.dx * 1.35;
    var moving = 0.0;
    final force = <String, Offset>{};
    for (final n in list) {
      force[n.id] = Offset.zero;
    }
    // Attached things: a stiff spring to their slot in a row under the
    // device. The device feels the pull too, so a bumped thing moves it.
    for (final e in attached.entries) {
      if (!isOpen(e.key) || bodies[e.key] == null) continue;
      final k = e.value.length;
      for (var i = 0; i < k; i++) {
        final id = e.value[i].report.id;
        final b = bodies[id];
        if (b == null) continue;
        final slot = junction(e.key) + Offset((i - (k - 1) / 2) * (thingSize.width + 14), 30 + thingSize.height / 2);
        final pull = (slot - b.p) * 0.18;
        force[id] = force[id]! + pull;
        force[e.key] = force[e.key]! - pull * 0.25;
      }
    }
    for (var i = 0; i < list.length; i++) {
      final a = bodies[list[i].id]!;
      if (list[i].thing) continue;
      // A spring to the middle; this device sits nearest it.
      var f = force[list[i].id]! - a.p * (list[i].id == widget.myId ? 0.03 : 0.012);
      for (var j = 0; j < list.length; j++) {
        if (i == j || list[j].thing) continue;
        final b = bodies[list[j].id]!;
        var d = _box(list[i], a.p).center - _box(list[j], b.p).center;
        if (d.distance < 0.01) d = Offset((i - j).toDouble(), 1);
        final dist = d.distance, u = d / dist;
        // Overlap pushes hard; nearby nodes push gently; far ones not at all.
        final nx = d.dx / room.dx, ny = d.dy / room.dy;
        final reach = math.sqrt(nx * nx + ny * ny); // 1 = just touching
        if (reach < 1) f += u * (1 - reach) * 14;
        if (dist < reachR) f += u * (reachR - dist) * 0.025;
        // Family members drift towards each other, other families apart.
        if (list[i].family == list[j].family) {
          f -= d * 0.004;
        } else if (dist < reachR * 1.5) {
          f += u * (reachR * 1.5 - dist) * 0.012;
        }
      }
      force[list[i].id] = f;
    }
    for (final n in list) {
      final b = bodies[n.id]!;
      b.v = (b.v + force[n.id]!) * 0.72;
      if (b.v.distance > 24) b.v = b.v / b.v.distance * 24;
      b.p += b.v;
      moving = math.max(moving, b.v.distance);
      if (b.appear < 1) b.appear = math.min(1, b.appear + 1 / 30);
    }
    // Then make sure nothing overlaps: nudge overlapping pairs apart. A
    // device with its things open takes the room of the whole group.
    final g = (gap - 20) / 2;
    for (var pass = 0; pass < 3; pass++) {
      for (var i = 0; i < list.length; i++) {
        if (list[i].thing) continue;
        for (var j = i + 1; j < list.length; j++) {
          if (list[j].thing) continue;
          final a = bodies[list[i].id]!, b = bodies[list[j].id]!;
          final ra = _box(list[i], a.p).inflate(g), rb = _box(list[j], b.p).inflate(g);
          if (!ra.overlaps(rb)) continue;
          final ox = math.min(ra.right, rb.right) - math.max(ra.left, rb.left);
          final oy = math.min(ra.bottom, rb.bottom) - math.max(ra.top, rb.top);
          final d = ra.center - rb.center;
          final push = ox < oy ? Offset(d.dx >= 0 ? ox / 2 : -ox / 2, 0) : Offset(0, d.dy >= 0 ? oy / 2 : -oy / 2);
          a.p += push;
          b.p -= push;
        }
      }
    }
    if (!userMoved) _fit(0.12);
    final arriving = list.any((n) => bodies[n.id]!.appear < 1);
    _calm = moving < 0.05 && !arriving ? _calm + 1 : 0;
    if (_calm > 40) physics.stop();
    setState(() {});
  }

  /// The room a device takes: its card, and the row of things under it
  /// when they are open.
  Rect _box(_Node n, Offset p) {
    final sz = sizeOf(n);
    final r = Rect.fromCenter(center: p, width: sz.width, height: sz.height);
    final k = isOpen(n.id) ? attached[n.id]?.length ?? 0 : 0;
    if (k == 0) return r;
    final w = k * thingSize.width + (k - 1) * 14;
    final top = p.dy + sz.height / 2 + 56;
    return r.expandToInclude(Rect.fromLTWH(p.dx - w / 2, top, w, thingSize.height));
  }

  Rect _bounds() {
    if (bodies.isEmpty) return Rect.zero;
    final sz = nodeSize;
    var r = Rect.fromCenter(center: bodies.values.first.p, width: sz.width, height: sz.height);
    for (final n in nodes) {
      final b = bodies[n.id];
      if (b == null) continue;
      final s = sizeOf(n);
      r = r.expandToInclude(Rect.fromCenter(center: b.p, width: s.width, height: s.height));
      if (attached.containsKey(n.id)) r = r.expandToInclude(Rect.fromCenter(center: junction(n.id), width: 120, height: 24));
    }
    // Room for the family names above each group.
    return busy ? Rect.fromLTRB(r.left - 30, r.top - 64, r.right + 30, r.bottom + 30) : Rect.fromLTRB(r.left - 30, r.top - 30, r.right + 30, r.bottom + 44);
  }

  /// Eases the camera towards showing every node.
  void _fit(double ease) {
    if (area.isEmpty) return;
    final r = _bounds();
    final z = math.min(area.width / r.width, area.height / r.height).clamp(0.3, 1.1);
    final p = -r.center * z;
    zoom += (z - zoom) * ease;
    pan = Offset.lerp(pan, p, ease)!;
  }

  Offset _screen(Offset world) => Offset(area.width / 2, area.height / 2) + pan + world * zoom;
  Offset _world(Offset screen) => (screen - Offset(area.width / 2, area.height / 2) - pan) / zoom;

  // ---- view ---------------------------------------------------------------

  @override
  Widget build(BuildContext context) {
    if (widget.devices.isEmpty) {
      return const Center(child: Text('No devices yet.', style: TextStyle(color: C.muted)));
    }
    final names = {for (final d in widget.devices) d.report.id: d.report.name};
    final fam = <String, int>{};
    for (final d in widget.devices) {
      fam.update(familyOf(d.report), (n) => n + 1, ifAbsent: () => 1);
    }
    return Column(children: [
      Expanded(
        child: ClipRRect(
          borderRadius: BorderRadius.circular(18),
          child: DecoratedBox(
            decoration: BoxDecoration(
              color: const Color(0xFF15181D),
              border: Border.all(color: C.line.withValues(alpha: 0.7)),
              borderRadius: BorderRadius.circular(18),
            ),
            child: LayoutBuilder(builder: (context, box) {
              final resized = area != Size(box.maxWidth, box.maxHeight);
              area = Size(box.maxWidth, box.maxHeight);
              if (resized && !userMoved) _fit(1);
              return Stack(children: [
                Positioned.fill(child: _canvas()),
                for (final n in nodes) _placed(n),
                for (final h in attached.keys)
                  if (bodies[h] != null) _junctionTag(h),
                if (busy) ..._familyLabels(fam),
                Positioned(right: 10, bottom: 10, child: _controls()),
                Positioned(
                  left: 14,
                  bottom: 12,
                  child: Text('${widget.devices.length} devices', style: const TextStyle(color: C.muted, fontSize: 11.5, fontWeight: FontWeight.w500)),
                ),
              ]);
            }),
          ),
        ),
      ),
      const SizedBox(height: 10),
      AnimatedSwitcher(duration: const Duration(milliseconds: 200), child: _detail(names)),
    ]);
  }

  Widget _canvas() {
    return Listener(
      onPointerSignal: (e) {
        if (e is PointerScrollEvent) {
          final w = _world(e.localPosition);
          setState(() {
            userMoved = true;
            zoom = (zoom * (e.scrollDelta.dy > 0 ? 0.9 : 1.1)).clamp(0.25, 2.5);
            pan = e.localPosition - Offset(area.width / 2, area.height / 2) - w * zoom;
          });
        }
      },
      child: GestureDetector(
        behavior: HitTestBehavior.opaque,
        onScaleStart: (_) => _start = (pan, zoom),
        onScaleUpdate: (e) => setState(() {
          if (e.pointerCount > 1 || e.scale != 1) {
            final w = _world(e.localFocalPoint);
            zoom = (_start.$2 * e.scale).clamp(0.25, 2.5);
            pan = e.localFocalPoint - Offset(area.width / 2, area.height / 2) - w * zoom;
          }
          pan += e.focalPointDelta;
          if (e.focalPointDelta.distance > 0.5) userMoved = true;
        }),
        onTapUp: (e) => setState(() {
          selected = _hit(e.localPosition);
          if (selected == null) focus = null;
        }),
        child: AnimatedBuilder(
          animation: flow,
          builder: (_, _) => CustomPaint(
            painter: _MapPainter(this, flow.value),
          ),
        ),
      ),
    );
  }

  (Offset, double) _start = (Offset.zero, 1);

  /// The small hub under a device with things attached: shows how many,
  /// and opens or folds them.
  Widget _junctionTag(String host) {
    final c = _screen(junction(host));
    final list = attached[host]!;
    final open = isOpen(host);
    final worst = list.map((t) => t.level).reduce((a, b) => a.index >= b.index ? a : b);
    final appear = bodies[host]!.appear.clamp(0.0, 1.0);
    return Positioned(
      left: c.dx - 70,
      top: c.dy - 12,
      width: 140,
      height: 24,
      child: Opacity(
        opacity: appear,
        child: Transform.scale(
          scale: zoom.clamp(0.6, 1.2),
          child: Center(
            child: _AttachTag(open ? 'attached · ${list.length}' : '${list.length} attached', open: open, level: worst, onTap: () => _toggleThings(host)),
          ),
        ),
      ),
    );
  }

  Widget _placed(_Node n) {
    final b = bodies[n.id]!;
    final sz = sizeOf(n);
    final c = _screen(b.p);
    final t = Curves.easeOutBack.transform(b.appear.clamp(0.0, 1.0));
    return Positioned(
      left: c.dx - sz.width / 2,
      top: c.dy - sz.height / 2,
      width: sz.width,
      height: sz.height,
      child: Transform.scale(
        scale: zoom * (0.6 + 0.4 * t),
        child: Opacity(
          opacity: b.appear.clamp(0.0, 1.0),
          child: MouseRegion(
            cursor: SystemMouseCursors.click,
            onEnter: (_) => setState(() => focus = n.id),
            onExit: (_) => setState(() => focus = focus == n.id ? null : focus),
            child: GestureDetector(
              onTap: () {
                if (n.folded) return _toggleFamily(n.family);
                if (busy && !n.thing && focus != n.id) return setState(() => focus = n.id);
                widget.onOpen(n.id);
              },
              child: n.folded
                  ? _FamilyNode(n)
                  : n.thing
                      ? _ThingChip(n.s, focused: focus == n.id)
                  : busy
                      ? _Chip(n.s, isMe: n.id == widget.myId, focused: focus == n.id)
                      : _Card(n.s, isMe: n.id == widget.myId, lifted: focus == n.id),
            ),
          ),
        ),
      ),
    );
  }

  /// A small label over each family on a busy map; tap it to fold the family.
  List<Widget> _familyLabels(Map<String, int> fam) {
    final out = <Widget>[];
    for (final f in fam.keys) {
      if (folded.contains(f) || fam[f]! < 2) continue;
      final members = nodes.where((n) => n.family == f && !n.thing).toList();
      if (members.isEmpty) continue;
      var top = double.infinity, sum = 0.0;
      for (final m in members) {
        final c = _screen(bodies[m.id]!.p);
        top = math.min(top, c.dy - nodeSize.height * zoom / 2);
        sum += c.dx;
      }
      out.add(Positioned(
        left: sum / members.length - 90,
        top: top - 30,
        width: 180,
        child: Center(
          child: _FamilyTag('$f · ${fam[f]}', onTap: () => _toggleFamily(f)),
        ),
      ));
    }
    return out;
  }

  Widget _controls() {
    Widget b(IconData i, String tip, VoidCallback f) => Tooltip(
          message: tip,
          child: InkWell(
            borderRadius: BorderRadius.circular(8),
            onTap: f,
            child: SizedBox(width: 32, height: 30, child: Icon(i, size: 16, color: C.muted)),
          ),
        );
    return Container(
      decoration: BoxDecoration(
        color: const Color(0xFF1C2026),
        borderRadius: BorderRadius.circular(10),
        border: Border.all(color: const Color(0xFF2C323B)),
        boxShadow: [BoxShadow(color: Colors.black.withValues(alpha: 0.35), blurRadius: 12, offset: const Offset(0, 4))],
      ),
      child: Row(mainAxisSize: MainAxisSize.min, children: [
        b(Icons.remove, 'Zoom out', () => setState(() {
              userMoved = true;
              zoom = (zoom / 1.25).clamp(0.25, 2.5);
              pan = pan / 1.25;
            })),
        b(Icons.add, 'Zoom in', () => setState(() {
              userMoved = true;
              zoom = (zoom * 1.25).clamp(0.25, 2.5);
              pan = pan * 1.25;
            })),
        b(Icons.fit_screen_outlined, 'Show everything', () {
          userMoved = false;
          _wake();
        }),
      ]),
    );
  }

  Widget _detail(Map<String, String> names) {
    final e = selected;
    if (e != null) {
      final text = e.link != null
          ? e.link!.describe(names[e.link!.a] ?? e.link!.a, names[e.link!.b] ?? e.link!.b)
          : '${e.count} links between ${_label(e.a)} and ${_label(e.b)}. '
              '${e.level == Level.ok ? 'All of them work both ways.' : 'Some get no answer; open the family to see which.'}';
      return _LinkDetail(key: ValueKey(e), level: e.level, text: text, onClose: () => setState(() => selected = null));
    }
    final f = focus == null ? null : nodes.where((n) => n.id == focus).firstOrNull;
    if (busy && f != null && !f.folded) {
      final mine = edges.where((x) => x.a == f.id || x.b == f.id);
      final ok = mine.where((x) => x.level == Level.ok).length;
      return _LinkDetail(
        key: ValueKey(f.id),
        level: f.s.level,
        text: '${f.s.report.name} · ${f.s.title}. Links: $ok of ${mine.length} work both ways.',
        action: ('Open', () => widget.onOpen(f.id)),
        onClose: () => setState(() => focus = null),
      );
    }
    return SizedBox(
      key: const ValueKey('hint'),
      height: 40,
      child: Center(
        child: Text(
            busy
                ? 'Tap a device to see its links  ·  tap a family name to fold it  ·  pinch or scroll to zoom'
                : 'Dots show traffic each way  ·  tap a cable for details',
            textAlign: TextAlign.center,
            style: const TextStyle(color: C.muted, fontSize: 11.5)),
      ),
    );
  }

  String _label(String id) => id.startsWith('family:') ? id.substring(7) : nodes.where((n) => n.id == id).firstOrNull?.s.report.name ?? id;

  _Edge? _hit(Offset p) {
    _Edge? best;
    var bestD = 14.0;
    for (final e in edges) {
      if (busy && e.level == Level.ok && focus != e.a && focus != e.b) continue;
      final a = bodies[e.a], b = bodies[e.b];
      if (a == null || b == null) continue;
      final d = _segDist(p, _screen(a.p), _screen(b.p));
      if (d < bestD) {
        bestD = d;
        best = e;
      }
    }
    return best;
  }

  static double _segDist(Offset p, Offset a, Offset b) {
    final ab = b - a;
    final len2 = ab.dx * ab.dx + ab.dy * ab.dy;
    if (len2 == 0) return (p - a).distance;
    final t = (((p - a).dx * ab.dx + (p - a).dy * ab.dy) / len2).clamp(0.0, 1.0);
    return (p - (a + ab * t)).distance;
  }
}

class _MapPainter extends CustomPainter {
  _MapPainter(this.m, this.t);
  final _NetworkMapState m;
  final double t;

  static const _quiet = Color(0xFF3B4450);

  @override
  void paint(Canvas canvas, Size size) {
    _grid(canvas, size);
    if (m.busy) _hulls(canvas);
    final rects = {
      for (final n in m.nodes)
        n.id: Rect.fromCenter(center: m._screen(m.bodies[n.id]!.p), width: m.sizeOf(n).width * m.zoom, height: m.sizeOf(n).height * m.zoom),
    };
    final groups = _ties(canvas, rects);
    final pills = <Rect>[...rects.values, ...groups.values];
    final order = [...m.edges]..sort((x, y) => _weight(x).compareTo(_weight(y)));
    for (final e in order) {
      // A device with its things open: cables plug into the whole group.
      final ra = groups[e.a] ?? rects[e.a], rb = groups[e.b] ?? rects[e.b];
      if (ra == null || rb == null) continue;
      final appear = math.min(m.bodies[e.a]!.appear, m.bodies[e.b]!.appear);
      if (appear <= 0) continue;
      final focused = m.focus == e.a || m.focus == e.b || identical(e, m.selected);
      final full = !m.busy || focused;
      // A busy map shows only this device's links, and a focused node's.
      final spoke = e.a == m.widget.myId || e.b == m.widget.myId;
      if (m.busy && !focused && (!spoke || m.focus != null)) continue;
      final a = _edge(ra, rb.center), b = _edge(rb, ra.center);
      final path = m.busy ? (Path()..moveTo(a.dx, a.dy)..lineTo(b.dx, b.dy)) : _curve(a, b, Offset(size.width / 2, size.height / 2));
      final color = e.level == Level.ok ? _quiet : C.of(e.level);

      if (!full) {
        // A busy map: working links are a whisper, problems a thin line.
        canvas.drawPath(
            path,
            Paint()
              ..style = PaintingStyle.stroke
              ..strokeWidth = 1.2
              ..color = e.level == Level.ok ? const Color(0xFF3B4450).withValues(alpha: 0.8 * appear) : color.withValues(alpha: 0.55 * appear));
        continue;
      }
      final sel = identical(e, m.selected);
      canvas.drawPath(
          path,
          Paint()
            ..style = PaintingStyle.stroke
            ..strokeWidth = sel ? 9 : 6
            ..strokeCap = StrokeCap.round
            ..color = (e.level == Level.ok ? Colors.black : color).withValues(alpha: (e.level == Level.ok ? 0.35 : 0.10) * appear));
      final line = Paint()
        ..style = PaintingStyle.stroke
        ..strokeWidth = sel ? 2.4 : 1.6
        ..strokeCap = StrokeCap.round
        ..color = (sel && e.level == Level.ok ? const Color(0xFF5A6575) : color).withValues(alpha: (e.level == Level.ok ? 1.0 : 0.8) * appear);
      final metric = path.computeMetrics().first;
      if (e.level == Level.fault || e.level == Level.inactive) {
        for (var d = 0.0; d < metric.length; d += 9) {
          canvas.drawPath(metric.extractPath(d, math.min(d + 4, metric.length)), line);
        }
      } else {
        canvas.drawPath(path, line);
      }
      if (appear < 1) continue;

      final dot = e.level == Level.ok ? C.flow : C.of(e.level);
      void travel(bool forward) {
        for (var k = 0; k < 2; k++) {
          final p = (t + k / 2) % 1.0;
          final at = metric.getTangentForOffset(metric.length * (forward ? p : 1 - p))!.position;
          final fade = math.sin(p * math.pi);
          canvas.drawCircle(at, 4.5, Paint()..color = dot.withValues(alpha: 0.18 * fade)..maskFilter = const MaskFilter.blur(BlurStyle.normal, 3));
          canvas.drawCircle(at, 1.9, Paint()..color = dot.withValues(alpha: 0.95 * fade));
        }
      }

      if (e.fwd) travel(true);
      if (e.back) travel(false);

      final label = e.count > 1 ? '${e.count} links' : e.ping != null ? '${e.ping} ms' : (e.level == Level.fault ? 'no answer' : null);
      if (label != null && metric.length > 70) {
        final tp = TextPainter(
          text: TextSpan(
              text: label,
              style: TextStyle(
                  fontFamily: 'Inter',
                  color: e.level == Level.fault ? C.fault : C.muted,
                  fontSize: 10.5,
                  fontWeight: FontWeight.w500,
                  fontFeatures: const [FontFeature.tabularFigures()])),
          textDirection: TextDirection.ltr,
        )..layout();
        var box = Rect.zero;
        for (final f in const [0.5, 0.36, 0.64, 0.28, 0.72]) {
          final at = metric.getTangentForOffset(metric.length * f)!.position;
          box = Rect.fromCenter(center: at, width: tp.width + 14, height: tp.height + 6);
          if (!pills.any((p) => p.inflate(3).overlaps(box))) break;
        }
        pills.add(box);
        final r = RRect.fromRectAndRadius(box, const Radius.circular(20));
        canvas.drawRRect(r, Paint()..color = const Color(0xFF15181D));
        canvas.drawRRect(r, Paint()..style = PaintingStyle.stroke..strokeWidth = 1..color = color.withValues(alpha: e.level == Level.ok ? 1 : 0.5));
        tp.paint(canvas, box.center - Offset(tp.width / 2, tp.height / 2));
      }
    }

    // A ring spreads out where a device has just arrived.
    for (final n in m.nodes) {
      final b = m.bodies[n.id]!;
      if (b.appear >= 1) continue;
      final c = m._screen(b.p);
      final r = (30 + 90 * b.appear) * m.zoom;
      canvas.drawCircle(c, r, Paint()..style = PaintingStyle.stroke..strokeWidth = 1.5..color = C.flow.withValues(alpha: 0.5 * (1 - b.appear)));
    }
  }

  /// Device to junction, junction to each attached thing: the hierarchy.
  /// Dots show what flows: data both ways over USB, power out of a UPS.
  Map<String, Rect> _ties(Canvas canvas, Map<String, Rect> rects) {
    final groups = <String, Rect>{};
    for (final e in m.attached.entries) {
      final host = rects[e.key];
      if (host == null) continue;
      final j = m._screen(m.junction(e.key));
      // A soft enclosure: the device and what hangs off it read as one unit.
      if (m.isOpen(e.key)) {
        var box = host;
        for (final th in e.value) {
          final r = rects[th.report.id];
          if (r != null) box = box.expandToInclude(r);
        }
        box = box.inflate(10 * m.zoom.clamp(0.6, 1.2));
        groups[e.key] = box;
        final rr = RRect.fromRectAndRadius(box, Radius.circular(18 * m.zoom.clamp(0.6, 1.2)));
        canvas.drawRRect(rr, Paint()..color = Colors.white.withValues(alpha: 0.025));
        canvas.drawRRect(rr, Paint()..style = PaintingStyle.stroke..strokeWidth = 1..color = Colors.white.withValues(alpha: 0.07));
      }
      final stem = Paint()
        ..style = PaintingStyle.stroke
        ..strokeWidth = 1.4
        ..strokeCap = StrokeCap.round
        ..color = const Color(0xFF4A5562);
      canvas.drawLine(Offset(j.dx, host.bottom + 2), j, stem);
      if (!m.isOpen(e.key)) continue;
      for (final th in e.value) {
        final r = rects[th.report.id];
        if (r == null) continue;
        final appear = m.bodies[th.report.id]!.appear;
        final to = Offset(r.center.dx, r.top - 2);
        final mid = (j.dy + to.dy) / 2;
        final path = Path()
          ..moveTo(j.dx, j.dy)
          ..cubicTo(j.dx, mid, to.dx, mid, to.dx, to.dy);
        final level = th.level;
        final color = level == Level.ok ? const Color(0xFF4A5562) : C.of(level);
        final line = Paint()
          ..style = PaintingStyle.stroke
          ..strokeWidth = 1.4
          ..strokeCap = StrokeCap.round
          ..color = color.withValues(alpha: appear);
        final metric = path.computeMetrics().first;
        if (level == Level.inactive) {
          for (var d = 0.0; d < metric.length; d += 8) {
            canvas.drawPath(metric.extractPath(d, math.min(d + 3, metric.length)), line);
          }
          continue;
        }
        canvas.drawPath(path, line);
        if (appear < 1) continue;
        final links = th.report.node?.links ?? const <NodeLink>[];
        final power = links.any((l) => l.via == 'power' && l.dir == 'out');
        final data = links.any((l) => l.via == 'usb' && l.dir == 'both');
        void dot(double p, Color c) {
          final at = metric.getTangentForOffset(metric.length * p)!.position;
          final fade = math.sin(p * math.pi);
          canvas.drawCircle(at, 4, Paint()..color = c.withValues(alpha: 0.2 * fade)..maskFilter = const MaskFilter.blur(BlurStyle.normal, 3));
          canvas.drawCircle(at, 1.7, Paint()..color = c.withValues(alpha: 0.95 * fade));
        }
        // Power climbs from the UPS up to the device; data runs both ways.
        if (power) dot(1 - (t * 1.6 % 1.0), const Color(0xFFE8B949));
        if (data) {
          dot(t * 1.3 % 1.0, C.flow);
          dot(1 - ((t * 1.3 + 0.5) % 1.0), C.flow);
        }
      }
    }
    return groups;
  }

  int _weight(_Edge e) => (m.focus == e.a || m.focus == e.b ? 10 : 0) + e.level.index;

  void _grid(Canvas canvas, Size size) {
    final p = Paint()..color = Colors.white.withValues(alpha: 0.045);
    final step = 22.0 * m.zoom.clamp(0.6, 2.0);
    final o = Offset(size.width / 2, size.height / 2) + m.pan;
    final sx = o.dx % step, sy = o.dy % step;
    for (var y = sy; y < size.height; y += step) {
      for (var x = sx; x < size.width; x += step) {
        canvas.drawCircle(Offset(x, y), 0.9, p);
      }
    }
  }

  /// A soft rounded outline around each family with two or more members.
  void _hulls(Canvas canvas) {
    final fam = <String, List<_Node>>{};
    for (final n in m.nodes) {
      if (!n.folded && !n.thing) fam.putIfAbsent(n.family, () => []).add(n);
    }
    for (final list in fam.values) {
      if (list.length < 2) continue;
      final pts = <Offset>[];
      for (final n in list) {
        final r = Rect.fromCenter(center: m._screen(m.bodies[n.id]!.p), width: m.sizeOf(n).width * m.zoom, height: m.sizeOf(n).height * m.zoom).inflate(6);
        pts.addAll([r.topLeft, r.topRight, r.bottomLeft, r.bottomRight]);
      }
      final hull = _convex(pts);
      final path = Path()..addPolygon(hull, true);
      final pad = 16 * m.zoom.clamp(0.6, 1.5);
      canvas.saveLayer(null, Paint()..color = Colors.white.withValues(alpha: 0.035));
      final solid = Paint()..color = Colors.white;
      canvas.drawPath(path, solid);
      canvas.drawPath(path, Paint()..color = Colors.white..style = PaintingStyle.stroke..strokeWidth = pad * 2..strokeJoin = StrokeJoin.round);
      canvas.restore();
    }
  }

  static List<Offset> _convex(List<Offset> pts) {
    final p = [...pts]..sort((a, b) => a.dx != b.dx ? a.dx.compareTo(b.dx) : a.dy.compareTo(b.dy));
    double cross(Offset o, Offset a, Offset b) => (a.dx - o.dx) * (b.dy - o.dy) - (a.dy - o.dy) * (b.dx - o.dx);
    final lower = <Offset>[], upper = <Offset>[];
    for (final x in p) {
      while (lower.length >= 2 && cross(lower[lower.length - 2], lower.last, x) <= 0) {
        lower.removeLast();
      }
      lower.add(x);
    }
    for (final x in p.reversed) {
      while (upper.length >= 2 && cross(upper[upper.length - 2], upper.last, x) <= 0) {
        upper.removeLast();
      }
      upper.add(x);
    }
    return [...lower..removeLast(), ...upper..removeLast()];
  }

  static Offset _edge(Rect r, Offset to) {
    final c = r.center, d = to - c;
    if (d == Offset.zero) return c;
    final sx = d.dx == 0 ? double.infinity : (r.width / 2 + 3) / d.dx.abs();
    final sy = d.dy == 0 ? double.infinity : (r.height / 2 + 3) / d.dy.abs();
    return c + d * math.min(sx, sy);
  }

  /// A gentle curve that bows away from the middle of the map.
  static Path _curve(Offset a, Offset b, Offset centre) {
    final v = b - a;
    final len = v.distance;
    var n = len == 0 ? Offset.zero : Offset(-v.dy, v.dx) / len;
    final m0 = (a + b) / 2 - centre;
    if (m0.dx * n.dx + m0.dy * n.dy < 0) n = -n;
    final bow = n * len * 0.08;
    final c1 = a + v * 0.3 + bow, c2 = a + v * 0.7 + bow;
    return Path()
      ..moveTo(a.dx, a.dy)
      ..cubicTo(c1.dx, c1.dy, c2.dx, c2.dy, b.dx, b.dy);
  }

  @override
  bool shouldRepaint(_MapPainter o) => true;
}

/// A device on a busy map: one compact line.
class _Chip extends StatelessWidget {
  const _Chip(this.s, {required this.isMe, required this.focused});
  final DeviceStatus s;
  final bool isMe, focused;

  @override
  Widget build(BuildContext context) {
    final r = s.report;
    final problem = s.level != Level.ok;
    final sub = problem ? s.title : _cardWhat(r) + (isMe ? ' · this device' : '');
    return AnimatedContainer(
      duration: const Duration(milliseconds: 160),
      padding: const EdgeInsets.fromLTRB(7, 6, 8, 6),
      decoration: BoxDecoration(
        borderRadius: BorderRadius.circular(12),
        gradient: const LinearGradient(begin: Alignment.topCenter, end: Alignment.bottomCenter, colors: [Color(0xFF232830), Color(0xFF1B1F25)]),
        border: Border.all(
            color: focused
                ? C.text.withValues(alpha: 0.5)
                : problem
                    ? C.of(s.level).withValues(alpha: 0.55)
                    : isMe
                        ? C.flow.withValues(alpha: 0.45)
                        : const Color(0xFF323944)),
        boxShadow: [BoxShadow(color: Colors.black.withValues(alpha: 0.4), blurRadius: 14, offset: const Offset(0, 6))],
      ),
      child: Row(children: [
        Container(
          width: 30,
          height: 30,
          decoration: BoxDecoration(color: const Color(0xFF14171B), borderRadius: BorderRadius.circular(8), border: Border.all(color: const Color(0xFF2C323B))),
          child: Icon(kindIcon(r.kind, r.name), size: 15, color: s.fresh ? C.text : C.muted),
        ),
        const SizedBox(width: 8),
        Expanded(
          child: Column(mainAxisAlignment: MainAxisAlignment.center, crossAxisAlignment: CrossAxisAlignment.start, children: [
            Text(r.name, maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 12.5, fontWeight: FontWeight.w600, height: 1.2)),
            Text(sub, maxLines: 1, overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 10, color: problem ? C.of(s.level) : C.muted, height: 1.3)),
          ]),
        ),
        _Pulse(s.level, live: r.live),
      ]),
    );
  }
}

/// A folded family: a small stack of cards with a count.
class _FamilyNode extends StatelessWidget {
  const _FamilyNode(this.n);
  final _Node n;

  @override
  Widget build(BuildContext context) {
    final s = n.s;
    final problem = s.level != Level.ok;
    Widget sheet(double dx, double dy, double a) => Positioned(
          left: dx,
          right: -dx,
          top: dy,
          bottom: -dy,
          child: Container(
            decoration: BoxDecoration(
              borderRadius: BorderRadius.circular(12),
              color: Color.lerp(const Color(0xFF1B1F25), const Color(0xFF15181D), a),
              border: Border.all(color: const Color(0xFF2C323B)),
            ),
          ),
        );
    return Stack(clipBehavior: Clip.none, children: [
      sheet(6, 6, 0.6),
      sheet(3, 3, 0.3),
      Positioned.fill(
        child: Container(
          padding: const EdgeInsets.fromLTRB(7, 6, 10, 6),
          decoration: BoxDecoration(
            borderRadius: BorderRadius.circular(12),
            gradient: const LinearGradient(begin: Alignment.topCenter, end: Alignment.bottomCenter, colors: [Color(0xFF262C35), Color(0xFF1D2128)]),
            border: Border.all(color: problem ? C.of(s.level).withValues(alpha: 0.55) : const Color(0xFF3A424E)),
            boxShadow: [BoxShadow(color: Colors.black.withValues(alpha: 0.4), blurRadius: 14, offset: const Offset(0, 6))],
          ),
          child: Row(children: [
            Container(
              width: 30,
              height: 30,
              alignment: Alignment.center,
              decoration: BoxDecoration(color: const Color(0xFF14171B), borderRadius: BorderRadius.circular(8), border: Border.all(color: const Color(0xFF2C323B))),
              child: Text('${n.members.length}', style: const TextStyle(fontSize: 13, fontWeight: FontWeight.w700, fontFeatures: [FontFeature.tabularFigures()])),
            ),
            const SizedBox(width: 8),
            Expanded(
              child: Column(mainAxisAlignment: MainAxisAlignment.center, crossAxisAlignment: CrossAxisAlignment.start, children: [
                Text(n.family, maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 12.5, fontWeight: FontWeight.w600, height: 1.2)),
                Text(s.title, maxLines: 1, overflow: TextOverflow.ellipsis, style: TextStyle(fontSize: 10, color: problem ? C.of(s.level) : C.muted, height: 1.3)),
              ]),
            ),
            const Icon(Icons.unfold_more, size: 15, color: C.muted),
          ]),
        ),
      ),
    ]);
  }
}

class _FamilyTag extends StatelessWidget {
  const _FamilyTag(this.text, {required this.onTap});
  final String text;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) => MouseRegion(
        cursor: SystemMouseCursors.click,
        child: GestureDetector(
          onTap: onTap,
          child: Container(
            padding: const EdgeInsets.fromLTRB(9, 3, 6, 3),
            decoration: BoxDecoration(
              color: const Color(0xFF1C2026),
              borderRadius: BorderRadius.circular(20),
              border: Border.all(color: const Color(0xFF2C323B)),
            ),
            child: Row(mainAxisSize: MainAxisSize.min, children: [
              Text(text.toUpperCase(), style: const TextStyle(fontSize: 9.5, letterSpacing: 0.9, fontWeight: FontWeight.w600, color: C.muted)),
              const SizedBox(width: 4),
              const Icon(Icons.unfold_less, size: 13, color: C.muted),
            ]),
          ),
        ),
      );
}

String _cardWhat(Report r) {
  final os = r.os ?? '';
  if (r.kind == 'android') return kindIcon(r.kind, r.name) == Icons.tablet_android ? 'Tablet' : 'Phone';
  if (os.contains('Docker')) return 'Server · Docker';
  return r.kind == 'windows' ? 'Windows' : r.kind == 'linux' ? 'Linux' : r.kind;
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
  const _LinkDetail({super.key, required this.level, required this.text, required this.onClose, this.action});
  final Level level;
  final String text;
  final VoidCallback onClose;
  final (String, VoidCallback)? action;

  @override
  Widget build(BuildContext context) => Container(
        padding: const EdgeInsets.fromLTRB(14, 6, 4, 6),
        decoration: BoxDecoration(
          color: C.surface,
          borderRadius: BorderRadius.circular(12),
          border: Border.all(color: level == Level.ok ? C.line : C.of(level).withValues(alpha: 0.5)),
        ),
        child: Row(children: [
          StatusDot(level, size: 8),
          const SizedBox(width: 12),
          Expanded(child: Text(text, style: const TextStyle(fontSize: 12.5, height: 1.35))),
          if (action != null) TextButton(onPressed: action!.$2, child: Text(action!.$1)),
          IconButton(onPressed: onClose, icon: const Icon(Icons.close, size: 16, color: C.muted)),
        ]),
      );
}

/// An attached thing: smaller and flatter than a device, hanging below it.
class _ThingChip extends StatelessWidget {
  const _ThingChip(this.s, {required this.focused});
  final DeviceStatus s;
  final bool focused;

  @override
  Widget build(BuildContext context) {
    final r = s.report;
    final problem = s.level == Level.warn || s.level == Level.fault;
    final off = s.level == Level.inactive;
    final head = _thingHeadline(r);
    final sub = problem || off ? s.title : head ?? s.title;
    return AnimatedContainer(
      duration: const Duration(milliseconds: 160),
      padding: const EdgeInsets.fromLTRB(6, 5, 9, 5),
      decoration: BoxDecoration(
        borderRadius: BorderRadius.circular(10),
        color: const Color(0xFF1A1E24),
        border: Border.all(
            color: focused
                ? C.text.withValues(alpha: 0.5)
                : problem
                    ? C.of(s.level).withValues(alpha: 0.55)
                    : const Color(0xFF2E353F)),
        boxShadow: [BoxShadow(color: Colors.black.withValues(alpha: 0.3), blurRadius: 10, offset: const Offset(0, 4))],
      ),
      child: Opacity(
        opacity: off ? 0.6 : 1,
        child: Row(children: [
          Container(
            width: 26,
            height: 26,
            decoration: BoxDecoration(color: const Color(0xFF14171B), borderRadius: BorderRadius.circular(7), border: Border.all(color: const Color(0xFF2C323B))),
            child: Icon(thingIcon(r.node?.type), size: 14, color: problem ? C.of(s.level) : C.text),
          ),
          const SizedBox(width: 8),
          Expanded(
            child: Column(mainAxisAlignment: MainAxisAlignment.center, crossAxisAlignment: CrossAxisAlignment.start, children: [
              Text(r.name, maxLines: 1, overflow: TextOverflow.ellipsis, style: const TextStyle(fontSize: 11.5, fontWeight: FontWeight.w600, height: 1.2)),
              Text(sub, maxLines: 1, overflow: TextOverflow.ellipsis,
                  style: TextStyle(fontSize: 9.5, color: problem ? C.of(s.level) : C.muted, height: 1.3, fontFeatures: const [FontFeature.tabularFigures()])),
            ]),
          ),
        ]),
      ),
    );
  }
}

/// One line that says the most about a thing: a UPS's charge and load, an
/// audio interface's sample rate.
String? _thingHeadline(Report r) {
  Object? v(String k) => r.readings.where((x) => x.key == k).firstOrNull?.value;
  String n(Object? x, String unit) => x is num ? '${x.round()}$unit' : '';
  switch (r.node?.type) {
    case 'ups':
      final parts = [
        if (v('ups.status') != null) v('ups.status') == 'mains' ? 'mains' : 'on ${v('ups.status')}',
        if (v('ups.charge') != null) n(v('ups.charge'), '%'),
        if (v('ups.load') != null) '${n(v('ups.load'), '%')} load',
      ];
      return parts.isEmpty ? null : parts.join(' · ');
    case 'audio-interface':
      final rate = r.readings.where((x) => x.key.endsWith('.rate') && x.value is num).firstOrNull?.value;
      final run = r.readings.any((x) => x.key.endsWith('.state') && x.value == 'running');
      return [if (rate is num) '${(rate / 1000).toStringAsFixed(rate % 1000 == 0 ? 0 : 1)} kHz', run ? 'streaming' : 'idle'].join(' · ');
  }
  return null;
}

/// The hub under a device with things attached.
class _AttachTag extends StatelessWidget {
  const _AttachTag(this.text, {required this.open, required this.level, required this.onTap});
  final String text;
  final bool open;
  final Level level;
  final VoidCallback onTap;

  @override
  Widget build(BuildContext context) {
    final problem = level == Level.warn || level == Level.fault;
    return MouseRegion(
      cursor: SystemMouseCursors.click,
      child: GestureDetector(
        onTap: onTap,
        child: Container(
          padding: const EdgeInsets.fromLTRB(8, 3, 6, 3),
          decoration: BoxDecoration(
            color: const Color(0xFF15181D),
            borderRadius: BorderRadius.circular(20),
            border: Border.all(color: problem ? C.of(level).withValues(alpha: 0.6) : const Color(0xFF3A434F)),
          ),
          child: Row(mainAxisSize: MainAxisSize.min, children: [
            Icon(Icons.usb, size: 11, color: problem ? C.of(level) : C.muted),
            const SizedBox(width: 4),
            Text(text, style: TextStyle(fontSize: 10.5, fontWeight: FontWeight.w500, color: problem ? C.of(level) : C.muted)),
            Icon(open ? Icons.expand_less : Icons.expand_more, size: 13, color: C.muted),
          ]),
        ),
      ),
    );
  }
}
