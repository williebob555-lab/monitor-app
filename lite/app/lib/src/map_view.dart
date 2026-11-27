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
  _Node(this.id, this.s, this.family, {this.members = const [], this.parent, this.host});
  final String id;
  final DeviceStatus s;
  final String family;
  final List<DeviceStatus> members; // non-empty for a folded family
  final String? parent; // set for software that shares its host's box
  final String? host; // set for attached hardware: its own node, cabled to the host
  bool get folded => members.isNotEmpty;
  bool get thing => parent != null;
  bool get hw => host != null;
  bool get small => thing || hw;
}

/// A cable between two nodes.
class _Edge {
  _Edge(this.a, this.b, this.level, this.fwd, this.back, this.ping, this.link, this.count, {this.via, this.detail});
  final String a, b;
  final Level level;
  final bool fwd, back;
  final int? ping;
  final Link? link; // null when it stands for several links
  final int count;
  final String? via; // attached hardware: "usb", "power"...
  final String? detail;
}

/// Things that run on a host and share its box on the map. Hardware gets a
/// node of its own instead.
const _software = {'compose-project', 'container', 'service'};
const _otherSystemHeight = 44.0;

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
  String? selDev; // device whose network cable was tapped
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
  Size sizeOf(_Node n) => n.small ? thingSize : Size(nodeSize.width, _hostHeight(n.id));

  double _hostHeight(String id) =>
      nodeSize.height + (otherSystem.containsKey(id) ? _otherSystemHeight : 0);

  Map<String, DeviceStatus> otherSystem = {};

  /// The software in [host]'s box.
  List<DeviceStatus> boxed(String host) => [for (final t in attached[host] ?? const <DeviceStatus>[]) if (t.report.isThing) t];

  /// Where the junction under [host] sits, in map coordinates.
  Offset junction(String host) => bodies[host]!.p + Offset(0, _hostHeight(host) / 2 + 26);

  void _toggleThings(String host) {
    setState(() {
      toggled.contains(host) ? toggled.remove(host) : toggled.add(host);
      _rebuild();
    });
  }

  /// Many physical hosts: fold software and show links on demand.
  bool get busy => widget.devices.length - otherSystem.length > 7;
  Size get nodeSize => Size(area.width < 460 ? 184 : 208, 60);

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
    final installs = [...devices]..sort((a, b) {
      final time = b.report.time.compareTo(a.report.time);
      return time != 0 ? time : a.report.id.compareTo(b.report.id);
    });
    otherSystem = {};
    final primaryByTwin = <String, String>{};
    for (final d in installs) {
      final r = d.report;
      if (primaryByTwin.containsKey(r.id) || r.machine == null || r.kind == 'android') continue;
      final twin = installs.where((o) {
        final other = o.report;
        if (other.id == r.id ||
            other.machine != r.machine ||
            other.kind == 'android' ||
            otherSystem.containsKey(other.id) ||
            primaryByTwin.containsKey(other.id)) {
          return false;
        }
        if (r.os != null && other.os != null) return r.os != other.os;
        return r.node?.links.any((l) => l.via == 'dual-boot' && l.to == other.id) == true ||
            other.node?.links.any((l) => l.via == 'dual-boot' && l.to == r.id) == true;
      }).firstOrNull;
      if (twin != null) {
        otherSystem[r.id] = twin;
        primaryByTwin[twin.report.id] = r.id;
      }
    }
    final byFamily = <String, List<DeviceStatus>>{};
    for (final d in devices) {
      if (!primaryByTwin.containsKey(d.report.id)) {
        byFamily.putIfAbsent(familyOf(d.report), () => []).add(d);
      }
    }
    folded.removeWhere((f) => (byFamily[f]?.length ?? 0) < 2);
    final owner = <String, String>{}; // device id -> node id
    final out = <_Node>[];
    for (final d in devices) {
      if (primaryByTwin.containsKey(d.report.id)) continue;
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
      final a = owner[l.a], b = owner[l.b];
      if (a == null || b == null || a == b) continue;
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
      final declaredParent = t.report.node?.parent;
      final parent = primaryByTwin[declaredParent] ?? declaredParent;
      if (parent == null || owner[parent] != parent) continue;
      final host = out.firstWhere((n) => n.id == parent);
      if (_software.contains(t.report.node?.type)) {
        attached.putIfAbsent(parent, () => []).add(t);
        continue;
      }
      // Hardware: its own node, with a cable to the host for each way it is
      // connected (USB, power...), as its file declares.
      out.add(_Node(t.report.id, t, host.family, host: parent));
      final ls = [for (final l in t.report.node?.links ?? const <NodeLink>[]) if (l.to == declaredParent) l];
      final vias = {for (final l in ls) l.via};
      final reportedHost = parent == declaredParent
          ? host.s.report.name
          : otherSystem[parent]!.report.name;
      edges.add(_Edge(t.report.id, parent, t.level == Level.inactive ? Level.inactive : Level.ok, true,
          vias.any((v) => v != 'power') && ls.any((l) => l.dir != 'out'), null, null, 1,
          via: vias.isEmpty ? 'attached' : vias.map((v) => v == 'usb' ? 'USB' : v).join(' + '),
          detail: '${t.report.name} → $reportedHost: ${ls.isEmpty ? 'attached' : ls.map((l) => '${l.via == 'usb' ? 'USB' : l.via}${l.detail != null ? ' (${l.detail})' : ''}').join(', ')}.'));
    }
    for (final e in attached.entries) {
      if (!isOpen(e.key)) continue;
      final host = out.firstWhere((n) => n.id == e.key);
      for (final t in boxed(e.key)) {
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
          if (m.family == n.family && !m.small && bodies.containsKey(m.id)) bodies[m.id]!.p,
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
      if (n.hw && bodies[n.host] != null) start = bodies[n.host]!.p + Offset(jitter.dx >= 0 ? 260 : -260, jitter.dy / 4);
      bodies[n.id] = _Body(start)..appear = bodies.isEmpty ? 1 : 0;
    }
    if (bodies.values.every((b) => b.appear == 0)) {
      for (final b in bodies.values) {
        b.appear = 1;
      }
    }
    final changed = nodes.length != out.length || !nodes.every((n) => ids.contains(n.id));
    nodes = out;
    netLevel.clear();
    // Not answering is expected of a device that isn't running right now.
    final resting = {...primaryByTwin.keys, for (final d in devices) if (d.level == Level.inactive) d.report.id};
    final ls = [for (final l in linksOf(devices)) if (!resting.contains(l.a) && !resting.contains(l.b)) l];
    for (final n in out.where((n) => !n.small)) {
      final mine = [for (final l in ls) if (n.folded ? n.members.any((d) => d.report.id == l.a || d.report.id == l.b) : (l.a == n.id || l.b == n.id)) l];
      final live = mine.where((l) => l.level != Level.inactive).toList();
      netLevel[n.id] = live.isEmpty
          ? Level.inactive
          : live.every((l) => l.level == Level.ok)
              ? Level.ok
              : live.any((l) => l.level == Level.ok || l.level == Level.warn)
                  ? Level.warn
                  : Level.fault;
    }
    syncLevel.clear();
    for (final n in out.where((n) => !n.small)) {
      Level one(DeviceStatus d) => d.report.syncthingLocal == false
          ? Level.fault
          : d.fresh
              ? Level.ok
              : Level.warn;
      final ms = n.folded ? n.members : [n.s];
      syncLevel[n.id] = ms.map(one).reduce((a, b) => a.index >= b.index ? a : b);
    }
    _layout();
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
    var moving = 0.0;
    for (final n in nodes) {
      final b = bodies[n.id]!;
      final to = targets[n.id] ?? b.p;
      final d = (to - b.p) * 0.2;
      b.p += d;
      moving = math.max(moving, d.distance);
      if (b.appear < 1) b.appear = math.min(1, b.appear + 1 / 30);
    }
    if (!userMoved) _fit(0.15);
    final arriving = nodes.any((n) => bodies[n.id]!.appear < 1);
    _calm = moving < 0.05 && !arriving ? _calm + 1 : 0;
    if (_calm > 40) physics.stop();
    setState(() {});
  }

  // ---- layout ---------------------------------------------------------------
  //
  // Fixed and deterministic: the private network on top, then one region per
  // kind of device (servers, computers, phones and tablets), devices in
  // rows by name inside it. Attached hardware sits beside its device;
  // software (Docker) in a box under it. Every device has one cable up to its
  // row's bus, the buses join a trunk down the left side, and the trunk goes
  // to the network. Which pairs reach each other shows on tap.

  static const tierOrder = ['Servers', 'Computers', 'Phones and tablets'];
  final targets = <String, Offset>{};
  final regions = <(String, Rect)>[]; // family, world rect (header inside the top)
  final buses = <(double, double, double)>[]; // y, x from, x to
  final busOf = <String, double>{}; // node id -> its row's bus y
  final _placedIds = <String>{};
  Offset hub = Offset.zero;
  double trunkX = 0;
  final branches = <(double, double)>[]; // x, lowest bus it feeds
  Rect layoutBounds = Rect.zero;

  /// How a device fares on the network: every peer reaches it both ways,
  /// some do, none do, or nobody has checked.
  final netLevel = <String, Level>{};

  /// How its Syncthing fares: running and its file arriving fresh here.
  final syncLevel = <String, Level>{};

  /// Phone width: one device per row, hardware stacked on its right.
  bool get narrow => area.width > 0 && area.width < 520;

  static const _cellGap = 40.0, _rowGap = 62.0, _pad = 16.0, _header = 34.0, _tierGap = 24.0, _sideGap = 72.0;

  List<_Node> hwOf(String host) => [for (final n in nodes) if (n.host == host) n];

  /// A device's cell: card, hardware beside, software box under.
  ({double w, double h, double left, double right}) _cell(_Node n) {
    final sz = sizeOf(n);
    final hw = hwOf(n.id);
    final l = narrow ? 0 : (hw.length + 1) ~/ 2, r = hw.length - l;
    final side = thingSize.width + _sideGap;
    var left = l > 0 ? side : 0.0, right = r > 0 ? side : 0.0;
    // The software box is centred under the card; make room for it.
    final k0 = attached.containsKey(n.id) && isOpen(n.id) ? boxed(n.id).length : 0;
    final half = math.max(k0 * thingSize.width + (k0 - 1) * 14 + 20, 170.0) / 2 - sz.width / 2;
    left = math.max(left, half);
    right = math.max(right, half);
    var w = sz.width + left + right;
    var h = math.max(sz.height, math.max(l, r) * (thingSize.height + 12) - 12);
    if (attached.containsKey(n.id)) {
      final k = isOpen(n.id) ? boxed(n.id).length : 0;
      h = sz.height + 26 + 14 + (k > 0 ? 30 + thingSize.height + 10 : 0);
    }
    return (w: w, h: h, left: left, right: right);
  }

  void _layout() {
    targets.clear();
    regions.clear();
    buses.clear();
    busOf.clear();
    final tops = [for (final n in nodes) if (!n.small) n];
    if (tops.isEmpty) return;
    final byTier = <String, List<_Node>>{};
    for (final n in tops) {
      byTier.putIfAbsent(n.family, () => []).add(n);
    }
    final tiers = [...tierOrder.where(byTier.containsKey), ...(byTier.keys.where((f) => !tierOrder.contains(f)).toList()..sort())];
    for (final f in tiers) {
      byTier[f]!.sort((a, b) => (a.folded ? '' : a.s.report.name.toLowerCase()).compareTo(b.folded ? '' : b.s.report.name.toLowerCase()));
    }
    final cells = {for (final n in tops) n.id: _cell(n)};
    final widest = cells.values.map((c) => c.w).reduce(math.max);

    // Rows for a given width; pick the width that lets the map be largest.
    List<List<List<_Node>>> rowsFor(double maxW) => [
          for (final f in tiers)
            () {
              final rows = <List<_Node>>[[]];
              var x = 0.0;
              for (final n in byTier[f]!) {
                final w = cells[n.id]!.w;
                if (rows.last.isNotEmpty && x + w > maxW) {
                  rows.add([]);
                  x = 0;
                }
                rows.last.add(n);
                x += w + _cellGap;
              }
              return rows;
            }(),
        ];
    double rowW(List<_Node> r) => r.fold(0.0, (a, n) => a + cells[n.id]!.w) + (r.length - 1) * _cellGap;
    double rowH(List<_Node> r) => r.map((n) => cells[n.id]!.h).reduce(math.max);
    final total = tops.fold(0.0, (a, n) => a + cells[n.id]!.w + _cellGap);
    double tierH(List<List<_Node>> t) => _header + t.fold(0.0, (b, r) => b + rowH(r) + _rowGap) + _pad;
    double tierW(List<List<_Node>> t) => t.map(rowW).reduce(math.max) + 2 * _pad;
    // Stacked (one region under another) or side by side (each region its
    // own column, joined along the top): whichever lets the map be largest
    // in this view. A wide screen gets side by side.
    var best = widest, bestZoom = 0.0, side = false;
    final view = area.isEmpty ? const Size(700, 500) : area;
    for (var i = 0; i <= (narrow ? 0 : 16); i++) {
      final maxW = widest + (total - widest) * i / 16;
      final rows = rowsFor(maxW);
      for (final s in [false, if (!narrow && tiers.length > 1) true]) {
        final w = s ? rows.fold(0.0, (a, t) => a + tierW(t) + 40) : rows.expand((t) => t).map(rowW).reduce(math.max) + 2 * _pad + 40;
        final h = 70 + (s ? rows.map(tierH).reduce(math.max) : rows.fold(0.0, (a, t) => a + tierH(t) + _tierGap));
        final z = math.min(view.width / w, view.height / h);
        if (z > bestZoom + 0.01) {
          bestZoom = z;
          best = maxW;
          side = s;
        }
      }
    }
    final rows = rowsFor(best);
    branches.clear();
    double placeTier(int t, double x0, double inner, double y) {
      final top = y;
      final branch = x0 - 18;
      y += _header;
      var lastBus = y;
      for (final row in rows[t]) {
        final bus = y + 2;
        lastBus = bus;
        y += _rowGap - 12;
        var x = x0 + _pad + (inner - rowW(row)) / 2;
        var last = x;
        for (final n in row) {
          final c = cells[n.id]!;
          final sz = sizeOf(n);
          final card = Offset(x + c.left + (c.w - c.left - c.right - sz.width) / 2 + sz.width / 2, y + sz.height / 2);
          targets[n.id] = card;
          busOf[n.id] = bus;
          last = card.dx;
          final hw = hwOf(n.id);
          for (var k = 0; k < hw.length; k++) {
            final side = narrow || k.isOdd ? 1.0 : -1.0;
            final rowsHw = narrow ? hw.length : side < 0 ? (hw.length + 1) ~/ 2 : hw.length ~/ 2;
            final dy = ((narrow ? k : k ~/ 2) - (rowsHw - 1) / 2) * (thingSize.height + 12);
            targets[hw[k].id] = card + Offset(side * (sz.width / 2 + _sideGap + thingSize.width / 2), dy);
          }
          final kids = isOpen(n.id) ? boxed(n.id) : const <DeviceStatus>[];
          for (var k = 0; k < kids.length; k++) {
            targets[kids[k].report.id] =
                card + Offset((k - (kids.length - 1) / 2) * (thingSize.width + 14), sz.height / 2 + 26 + 30 + thingSize.height / 2);
          }
          x += c.w + _cellGap;
        }
        buses.add((bus, branch, last));
        y += rowH(row) + 12;
      }
      y += _pad - 12;
      regions.add((tiers[t], Rect.fromLTRB(x0, top, x0 + inner + 2 * _pad, y)));
      branches.add((branch, lastBus));
      return y;
    }

    var right = 0.0, bottom = 0.0;
    if (side) {
      var x = 0.0;
      for (var t = 0; t < tiers.length; t++) {
        final inner = tierW(rows[t]) - 2 * _pad;
        bottom = math.max(bottom, placeTier(t, x, inner, 54));
        x += inner + 2 * _pad + 40;
      }
      right = x - 40;
    } else {
      final inner = rows.expand((t) => t).map(rowW).reduce(math.max);
      var y = 54.0;
      for (var t = 0; t < tiers.length; t++) {
        y = placeTier(t, 0, inner, y) + _tierGap;
      }
      bottom = y - _tierGap;
      right = inner + 2 * _pad;
    }
    trunkX = -18;
    hub = Offset(right / 2, 0);
    layoutBounds = Rect.fromLTRB(trunkX - 10, -20, right + 10, bottom + 30); // room for the zoom buttons
    for (final n in nodes) {
      if (bodies[n.id] != null && targets[n.id] != null && _placedIds.add(n.id)) {
        // Arrivals start in place and grow, rather than flying in.
        bodies[n.id]!.p = targets[n.id]!;
      }
    }
  }

  Rect _bounds() => layoutBounds.isEmpty ? Rect.fromCenter(center: Offset.zero, width: 400, height: 300) : layoutBounds;

  /// Eases the camera towards showing every node.
  void _fit(double ease) {
    if (area.isEmpty) return;
    final r = _bounds();
    final z = math.min(area.width / r.width, area.height / r.height).clamp(0.3, 1.5);
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
    final hostCount = widget.devices.length - otherSystem.length;
    final needsAttention = [
      for (final d in [...widget.devices, ...widget.things])
        if (!otherSystem.values.contains(d) && (d.level == Level.warn || d.level == Level.fault)) d,
    ];
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
              if (resized) {
                _layout();
                _wake();
              }
              if (resized && !userMoved) _fit(1);
              return Stack(children: [
                Positioned.fill(child: _canvas()),
                for (final n in nodes) _placed(n),
                for (final h in attached.keys)
                  if (bodies[h] != null) _junctionTag(h),
                ..._regionHeaders(),
                Positioned(right: 10, bottom: 10, child: _controls()),
                Positioned(
                  left: 14,
                  bottom: 12,
                  child: Text(
                    needsAttention.isEmpty
                        ? '$hostCount ${hostCount == 1 ? 'device' : 'devices'}'
                        : 'Attention needed · ${needsAttention.length}',
                    style: TextStyle(
                      color: needsAttention.isEmpty ? C.muted : C.warn,
                      fontSize: 11.5,
                      fontWeight: FontWeight.w500,
                    ),
                  ),
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
          selDev = selected == null ? _hitStub(e.localPosition) : null;
          if (selected == null && selDev == null) focus = null;
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
      left: c.dx - 130,
      top: c.dy - 12,
      width: 260,
      height: 24,
      child: Opacity(
        opacity: appear,
        child: Transform.scale(
          scale: zoom.clamp(0.6, 1.2),
          child: Center(
            child: _AttachTag(_tagText(list, open), open: boxed(host).isEmpty ? null : open, level: worst, onTap: () => _toggleThings(host)),
          ),
        ),
      ),
    );
  }

  /// The folded summary of the reported attached things.
  static String _tagText(List<DeviceStatus> list, bool open) {
    final parts = <String>[];
    var run = 0, all = 0, projects = 0;
    for (final d in list.where((d) => d.report.node?.type == 'compose-project')) {
      projects++;
      final m = RegExp(r'(\d+) of (\d+)').firstMatch('${d.report.readings.where((r) => r.key == 'cp.running').firstOrNull?.value ?? ''}');
      if (m != null) {
        run += int.parse(m.group(1)!);
        all += int.parse(m.group(2)!);
      }
    }
    if (projects > 0) parts.add(all > 0 ? 'docker $run/$all' : 'docker');
    final plugged = [for (final d in list) if (d.report.isThing && d.report.node?.type != 'compose-project') d];
    if (plugged.length <= 2) {
      for (final d in plugged) {
        parts.add(switch (d.report.node?.type) { 'ups' => 'UPS', 'audio-interface' => 'audio', _ => d.report.name });
      }
    } else {
      parts.add('${plugged.length} attached');
    }
    return parts.join(' · ');
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
                  : n.small
                      ? _ThingChip(n.s, focused: focus == n.id)
                  : _DeviceNode(
                      n.s,
                      isMe: n.id == widget.myId,
                      focused: focus == n.id,
                      otherSystem: otherSystem[n.id],
                      onOpen: widget.onOpen,
                    ),
            ),
          ),
        ),
      ),
    );
  }

  /// Each region's name in its top-left corner; with two or more devices,
  /// tap it to fold the region into one node.
  List<Widget> _regionHeaders() {
    // Count physical hosts represented by each family.
    final fam = <String, int>{};
    for (final n in nodes) {
      if (!n.small) fam.update(n.family, (c) => c + (n.folded ? n.members.length : 1), ifAbsent: () => n.folded ? n.members.length : 1);
    }
    return [
        for (final (f, r) in regions)
          Positioned(
            left: _screen(r.topLeft).dx + 10 * zoom,
            top: _screen(r.topLeft).dy + 8 * zoom,
            child: Transform.scale(
              scale: zoom.clamp(0.6, 1.2),
              alignment: Alignment.topLeft,
              child: (fam[f] ?? 0) >= 2
                  ? _FamilyTag('$f · ${fam[f]}', onTap: () => _toggleFamily(f))
                  : Text(f, style: const TextStyle(color: C.muted, fontSize: 11, fontWeight: FontWeight.w600, letterSpacing: 0.3)),
            ),
          ),
    ];
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
      final text = e.detail != null
          ? e.detail!
          : e.link != null
          ? e.link!.describe(names[e.link!.a] ?? e.link!.a, names[e.link!.b] ?? e.link!.b)
          : '${e.count} links between ${_label(e.a)} and ${_label(e.b)}. '
              '${e.level == Level.ok ? 'All of them work both ways.' : 'Some get no answer; open the family to see which.'}';
      return _LinkDetail(key: ValueKey(e), level: e.level, text: text, onClose: () => setState(() => selected = null));
    }
    final sd = selDev == null ? null : nodes.where((n) => n.id == selDev).firstOrNull;
    if (sd != null) {
      final mine = edges.where((x) => x.via == null && (x.a == sd.id || x.b == sd.id)).toList();
      final bad = [for (final x in mine) if (x.level != Level.ok) _label(x.a == sd.id ? x.b : x.a)];
      return _LinkDetail(
        key: ValueKey('net:${sd.id}'),
        level: netLevel[sd.id] ?? Level.inactive,
        text: '${_label(sd.id)} on the private network: ${mine.length - bad.length} of ${mine.length} links work both ways'
            '${bad.isEmpty ? '.' : '; not with ${bad.join(', ')}.'} Its links are drawn on the map; tap one for details.',
        onClose: () => setState(() => selDev = null),
      );
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
    return Padding(
      key: const ValueKey('hint'),
      padding: const EdgeInsets.symmetric(vertical: 8),
      child: Wrap(alignment: WrapAlignment.center, spacing: 14, runSpacing: 6, crossAxisAlignment: WrapCrossAlignment.center, children: [
        for (final (c, t) in const [
          (_MapPainter.pulseNet, 'network'),
          (_MapPainter.pulseSync, 'Syncthing'),
          (_MapPainter.layerUsb, 'USB'),
          (_MapPainter.pulsePower, 'power'),
        ])
          Row(mainAxisSize: MainAxisSize.min, children: [
            Container(width: 14, height: 2.5, color: c.withValues(alpha: 0.7)),
            const SizedBox(width: 5),
            Text(t, style: const TextStyle(color: C.muted, fontSize: 11.5)),
          ]),
        const Text('tap a trace for details', style: TextStyle(color: C.muted, fontSize: 11.5)),
      ]),
    );
  }

  String _label(String id) => id.startsWith('family:') ? id.substring(7) : nodes.where((n) => n.id == id).firstOrNull?.s.report.name ?? id;

  /// The device whose cable up to the network is under [p].
  String? _hitStub(Offset p) {
    for (final n in nodes) {
      final by = busOf[n.id], b = bodies[n.id];
      if (by == null || b == null) continue;
      final top = _screen(b.p - Offset(0, sizeOf(n).height / 2)), bus = _screen(Offset(b.p.dx, by));
      if (_segDist(p, bus, top) < 10) return n.id;
    }
    return null;
  }

  _Edge? _hit(Offset p) {
    _Edge? best;
    var bestD = 14.0;
    final sel = selDev ?? focus;
    for (final e in edges) {
      if (e.via == null && sel != e.a && sel != e.b) continue;
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
    final rects = {
      for (final n in m.nodes)
        n.id: Rect.fromCenter(center: m._screen(m.bodies[n.id]!.p), width: m.sizeOf(n).width * m.zoom, height: m.sizeOf(n).height * m.zoom),
    };
    _regions(canvas);
    _network(canvas, rects);
    _hardware(canvas, rects);
    final groups = _groups(rects);
    final pills = <Rect>[...rects.values, ...groups.values];
    final order = [...m.edges]..sort((x, y) => _weight(x).compareTo(_weight(y)));
    for (final e in order) {
      // Cables plug into the device card; they bend around rows of things.
      final ra = rects[e.a], rb = rects[e.b];
      if (ra == null || rb == null) continue;
      final appear = math.min(m.bodies[e.a]!.appear, m.bodies[e.b]!.appear);
      if (appear <= 0) continue;
      final who = m.selDev ?? m.focus;
      final focused = who == e.a || who == e.b || identical(e, m.selected);
      // Device to device: only the selected device's, over everything else.
      // Hardware cables always show.
      if (e.via != null || !focused) continue;
      final a = _edge(ra, rb.center), b = _edge(rb, ra.center);
      // A cable that would cross another device's group bends around it.
      final around = e.via != null ? (Path()..moveTo(a.dx, a.dy)..lineTo(b.dx, b.dy)) : _detour(a, b, [...groups.values]);
      final path = around ??
          (m.busy ? (Path()..moveTo(a.dx, a.dy)..lineTo(b.dx, b.dy)) : _curve(a, b, Offset(size.width / 2, size.height / 2)));
      final color = e.level == Level.ok ? _quiet : C.of(e.level);

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

      final label = e.via != null ? e.via! : e.count > 1 ? '${e.count} links' : e.ping != null ? '${e.ping} ms' : (e.level == Level.fault ? 'no answer' : null);
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

    _ties(canvas, rects, groups);

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
  /// For each device whose things are open: the strip holding its hub and
  /// row of things, which cables keep out of.
  Map<String, Rect> _groups(Map<String, Rect> rects) {
    final groups = <String, Rect>{};
    final z = m.zoom.clamp(0.6, 1.2);
    for (final e in m.attached.entries) {
      final host = rects[e.key];
      if (host == null || !m.isOpen(e.key)) continue;
      final j = m._screen(m.junction(e.key));
      var box = Rect.fromCenter(center: j, width: 150 * z, height: 22 * z);
      var any = false;
      for (final th in e.value) {
        final r = rects[th.report.id];
        if (r == null) continue;
        box = box.expandToInclude(r);
        any = true;
      }
      if (any) groups[e.key] = box.inflate(8 * z);
    }
    return groups;
  }

  void _ties(Canvas canvas, Map<String, Rect> rects, Map<String, Rect> groups) {
    for (final e in m.attached.entries) {
      final host = rects[e.key];
      if (host == null) continue;
      final j = m._screen(m.junction(e.key));
      // The device and what hangs off it read as one unit: a tight outline
      // around its card and the row under it, joined by a short neck. Only
      // that shape is filled, so there is no big empty slab around it.
      final row = groups[e.key];
      if (row != null) {
        final z = m.zoom.clamp(0.6, 1.2);
        final card = host.inflate(6 * z);
        final shape = Path.combine(
          PathOperation.union,
          Path.combine(
            PathOperation.union,
            Path()..addRRect(RRect.fromRectAndRadius(card, Radius.circular(14 * z))),
            Path()..addRect(Rect.fromLTRB(j.dx - 14 * z, card.bottom - 4, j.dx + 14 * z, row.top + 4)),
          ),
          Path()..addRRect(RRect.fromRectAndRadius(row, Radius.circular(14 * z))),
        );
        canvas.drawPath(shape, Paint()..color = const Color(0xFF15181D));
        canvas.drawPath(shape, Paint()..style = PaintingStyle.stroke..strokeWidth = 1..color = const Color(0xFF2E353F));
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
        final path = _trace([j, Offset(j.dx, mid), Offset(to.dx, mid), to], 8 * m.zoom);
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
  }

  int _weight(_Edge e) => (m.focus == e.a || m.focus == e.b ? 10 : 0) + e.level.index;

  /// One clean rectangle per kind of device; its name is a widget on top.
  void _regions(Canvas canvas) {
    final r = 14 * m.zoom.clamp(0.6, 1.2);
    for (final (_, w) in m.regions) {
      final box = RRect.fromRectAndRadius(Rect.fromPoints(m._screen(w.topLeft), m._screen(w.bottomRight)), Radius.circular(r));
      canvas.drawRRect(box, Paint()..color = Colors.white.withValues(alpha: 0.022));
      canvas.drawRRect(box, Paint()..style = PaintingStyle.stroke..strokeWidth = 1..color = const Color(0xFF282E36));
    }
  }

  // ---- the board -----------------------------------------------------------
  //
  // Drawn like a circuit board: devices are chips, connections are traces
  // with 45° bends, pads where they land and vias where they join a bus. Each
  // kind of connection is its own quiet layer; colour is kept for problems.

  static const board = Color(0xFF15181D);
  static const layerNet = Color(0xFF3D5A61); // private network (Tailscale)
  static const layerSync = Color(0xFF4A4E72); // Syncthing
  static const layerUsb = Color(0xFF5C6878);
  static const layerPower = Color(0xFF6A5734);
  static const pulseNet = Color(0xFF6FD3DB);
  static const pulseSync = Color(0xFF9EA6F0);
  static const pulsePower = Color(0xFFE8B949);

  /// An orthogonal path through [pts] with every corner cut at 45°.
  static Path _trace(List<Offset> pts, double c) {
    final path = Path()..moveTo(pts.first.dx, pts.first.dy);
    for (var i = 1; i < pts.length - 1; i++) {
      final p = pts[i], a = pts[i - 1], b = pts[i + 1];
      final da = (p - a), db = (b - p);
      final k = math.min(c, math.min(da.distance, db.distance) / 2);
      final before = da.distance == 0 ? p : p - da / da.distance * k;
      final after = db.distance == 0 ? p : p + db / db.distance * k;
      path
        ..lineTo(before.dx, before.dy)
        ..lineTo(after.dx, after.dy);
    }
    path.lineTo(pts.last.dx, pts.last.dy);
    return path;
  }

  /// One trace: a halo of board colour under it, so where layers cross the
  /// upper one reads as passing over, then the copper.
  void _copper(Canvas canvas, Path path, Color color, {double width = 1.6, bool dashed = false}) {
    final z = m.zoom.clamp(0.6, 1.4);
    canvas.drawPath(path, Paint()..style = PaintingStyle.stroke..strokeWidth = (width + 3) * z..color = board..strokeJoin = StrokeJoin.miter);
    final line = Paint()
      ..style = PaintingStyle.stroke
      ..strokeWidth = width * z
      ..strokeJoin = StrokeJoin.miter
      ..color = color;
    if (!dashed) {
      canvas.drawPath(path, line);
      return;
    }
    for (final metric in path.computeMetrics()) {
      for (var d = 0.0; d < metric.length; d += 7 * z) {
        canvas.drawPath(metric.extractPath(d, math.min(d + 3.5 * z, metric.length)), line);
      }
    }
  }

  void _via(Canvas canvas, Offset at, Color color) {
    final z = m.zoom.clamp(0.6, 1.4);
    canvas.drawCircle(at, 3.2 * z, Paint()..color = board);
    canvas.drawCircle(at, 3.2 * z, Paint()..style = PaintingStyle.stroke..strokeWidth = 1.4 * z..color = color);
  }

  void _pad(Canvas canvas, Offset at, Color color, {bool vertical = true}) {
    final z = m.zoom.clamp(0.6, 1.4);
    final r = Rect.fromCenter(center: at, width: (vertical ? 6 : 5) * z, height: (vertical ? 5 : 6) * z);
    canvas.drawRRect(RRect.fromRectAndRadius(r, Radius.circular(1.2 * z)), Paint()..color = color);
  }

  Color _layer(Level l, Color base) => l == Level.ok ? base : l == Level.inactive ? base.withValues(alpha: 0.5) : C.of(l);

  /// The private network and Syncthing: two traces from the hub chip, down
  /// a trunk on the left, a pair of buses over each row, and a pair of
  /// traces up from each device, each coloured by how that layer fares.
  void _network(Canvas canvas, Map<String, Rect> rects) {
    if (m.buses.isEmpty) return;
    final z = m.zoom.clamp(0.6, 1.4);
    final gap = 7 * m.zoom;
    final hub = m._screen(m.hub);
    final yNetHub = hub.dy - 4 * z, ySyncHub = hub.dy + 4 * z;
    final c = 8 * m.zoom;
    // One branch down the left of each region (all regions share one when
    // stacked), fed along the top from the hub.
    final byX = <double, double>{};
    for (final (x, b) in m.branches) {
      byX[x] = math.max(byX[x] ?? b, b);
    }
    final xs = byX.keys.toList()..sort();
    double sx(double wx) => m._screen(Offset(wx, 0)).dx;
    for (var i = 0; i < xs.length; i++) {
      final tNet = sx(xs[i]) - gap, tSync = tNet + gap;
      final low = m._screen(Offset(0, byX[xs[i]]!)).dy;
      final first = i == 0;
      final net = first
          ? _trace([Offset(hub.dx, yNetHub), Offset(tNet, yNetHub), Offset(tNet, low + gap)], c)
          : _trace([Offset(tNet, yNetHub), Offset(tNet, low + gap)], c);
      final sync = first
          ? _trace([Offset(hub.dx, ySyncHub), Offset(tSync, ySyncHub), Offset(tSync, low)], c)
          : _trace([Offset(tSync, ySyncHub), Offset(tSync, low)], c);
      _copper(canvas, sync, layerSync);
      _copper(canvas, net, layerNet);
      if (!first) {
        _via(canvas, Offset(tNet, yNetHub), layerNet);
        _via(canvas, Offset(tSync, ySyncHub), layerSync);
      }
    }
    if (xs.length > 1) {
      // The top run from the hub out to the farthest branch.
      final far = sx(xs.last);
      _copper(canvas, Path()..moveTo(hub.dx, ySyncHub)..lineTo(far, ySyncHub), layerSync);
      _copper(canvas, Path()..moveTo(hub.dx, yNetHub)..lineTo(far - gap, yNetHub), layerNet);
    }
    // Buses: Syncthing on the row line, the network a step below it.
    for (final (y, bx, x2) in m.buses) {
      final tNet = sx(bx) - gap, tSync = tNet + gap;
      final sy = m._screen(Offset(0, y)).dy, ny = sy + gap;
      final x = sx(x2) + gap;
      _copper(canvas, Path()..moveTo(tSync, sy)..lineTo(x, sy), layerSync);
      _copper(canvas, Path()..moveTo(tNet, ny)..lineTo(x, ny), layerNet);
      _via(canvas, Offset(tSync, sy), layerSync);
      _via(canvas, Offset(tNet, ny), layerNet);
    }
    for (final n in m.nodes) {
      if (n.small) continue;
      final r = rects[n.id], by = m.busOf[n.id];
      if (r == null || by == null) continue;
      final sy = m._screen(Offset(0, by)).dy, ny = sy + gap;
      final net = m.netLevel[n.id] ?? Level.inactive, sync = m.syncLevel[n.id] ?? Level.inactive;
      final sel = m.selDev == n.id;
      final xs = r.center.dx + gap / 2 + 3 * z, xn = r.center.dx - gap / 2 - 3 * z;
      final top = r.top - 1;
      final syncPath = Path()..moveTo(xs, top)..lineTo(xs, sy);
      final netPath = Path()..moveTo(xn, top)..lineTo(xn, ny);
      _copper(canvas, syncPath, _layer(sync, layerSync), dashed: sync == Level.fault, width: sel ? 2.4 : 1.6);
      _copper(canvas, netPath, _layer(net, layerNet), dashed: net == Level.fault || net == Level.inactive, width: sel ? 2.4 : 1.6);
      _via(canvas, Offset(xs, sy), _layer(sync, layerSync));
      _via(canvas, Offset(xn, ny), _layer(net, layerNet));
      _pad(canvas, Offset(xs, top), _layer(sync, layerSync));
      _pad(canvas, Offset(xn, top), _layer(net, layerNet));
    }
    // The hub chip.
    final tp = TextPainter(
      text: const TextSpan(text: 'Private network', style: TextStyle(fontFamily: 'Inter', color: C.text, fontSize: 11, fontWeight: FontWeight.w600)),
      textDirection: TextDirection.ltr,
    )..layout();
    final box = Rect.fromCenter(center: hub, width: (tp.width + 40) * z, height: 28 * z);
    final rr = RRect.fromRectAndRadius(box, Radius.circular(6 * z));
    canvas.drawRRect(rr, Paint()..color = const Color(0xFF1C2026));
    canvas.drawRRect(rr, Paint()..style = PaintingStyle.stroke..strokeWidth = 1..color = const Color(0xFF3A434F));
    canvas.drawCircle(Offset(box.left + 12 * z, hub.dy), 3 * z, Paint()..color = C.flow);
    canvas.save();
    canvas.translate(box.left + 22 * z, hub.dy - tp.height * z / 2);
    canvas.scale(z);
    tp.paint(canvas, Offset.zero);
    canvas.restore();
  }

  /// Attached hardware: one trace per connection (USB, power), side by side,
  /// from the device's edge to the thing's, with a jog where they differ.
  void _hardware(Canvas canvas, Map<String, Rect> rects) {
    final z = m.zoom.clamp(0.6, 1.4);
    for (final e in m.edges) {
      if (e.via == null) continue;
      final thing = rects[e.a], host = rects[e.b];
      if (thing == null || host == null) continue;
      final appear = math.min(m.bodies[e.a]!.appear, m.bodies[e.b]!.appear);
      if (appear < 1) continue;
      final node = m.nodes.where((n) => n.id == e.a).firstOrNull;
      final links = [for (final l in node?.s.report.node?.links ?? const <NodeLink>[]) if (l.to == node?.s.report.node?.parent) l];
      final kinds = links.isEmpty ? ['attached'] : {for (final l in links) l.via}.toList();
      final right = thing.center.dx > host.center.dx;
      final x1 = right ? host.right + 1 : host.left - 1, x2 = right ? thing.left - 1 : thing.right + 1;
      final mid = (x1 + x2) / 2;
      final dead = e.level == Level.inactive;
      for (var i = 0; i < kinds.length; i++) {
        final dy = (i - (kinds.length - 1) / 2) * 8 * z;
        final a = Offset(x1, (thing.center.dy + dy).clamp(host.top + 8 * z, host.bottom - 8 * z));
        final b = Offset(x2, thing.center.dy + dy);
        final path = _trace([a, Offset(mid, a.dy), Offset(mid, b.dy), b], 8 * m.zoom);
        final power = kinds[i] == 'power';
        final base = power ? layerPower : layerUsb;
        _copper(canvas, path, dead ? base.withValues(alpha: 0.5) : base, dashed: dead);
        _pad(canvas, a, base, vertical: false);
        _pad(canvas, b, base, vertical: false);
      }
    }
  }

  static Offset _edge(Rect r, Offset to) {
    final c = r.center, d = to - c;
    if (d == Offset.zero) return c;
    final sx = d.dx == 0 ? double.infinity : (r.width / 2 + 3) / d.dx.abs();
    final sy = d.dy == 0 ? double.infinity : (r.height / 2 + 3) / d.dy.abs();
    return c + d * math.min(sx, sy);
  }

  /// A curve from [a] to [b] that clears every box it would cross, or null
  /// when the straight way is clear.
  static Path? _detour(Offset a, Offset b, List<Rect> boxes) {
    final v = b - a;
    final len = v.distance;
    if (len < 1) return null;
    final n = Offset(-v.dy, v.dx) / len;
    var push = 0.0;
    for (final r in boxes) {
      // Only boxes that sit between the ends.
      final t = ((r.center - a).dx * v.dx + (r.center - a).dy * v.dy) / (len * len);
      if (t < 0 || t > 1) continue;
      final side = (r.center - a).dx * n.dx + (r.center - a).dy * n.dy; // signed distance
      final half = (r.width * n.dx.abs() + r.height * n.dy.abs()) / 2;
      if (side.abs() >= half + 4) continue;
      // Go round the side nearer the line; bezier peaks at 3/4 of the offset.
      final need = (side >= 0 ? -(half - side) : (half + side)) - (side >= 0 ? 14 : -14);
      final k = need / 0.75;
      if (k.abs() > push.abs()) push = k;
    }
    if (push == 0) return null;
    final c1 = a + v * 0.25 + n * push, c2 = a + v * 0.75 + n * push;
    return Path()
      ..moveTo(a.dx, a.dy)
      ..cubicTo(c1.dx, c1.dy, c2.dx, c2.dy, b.dx, b.dy);
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

/// A physical device, with separate detail pages for its OS installs.
class _DeviceNode extends StatelessWidget {
  const _DeviceNode(this.status, {
    required this.isMe,
    required this.focused,
    required this.otherSystem,
    required this.onOpen,
  });

  final DeviceStatus status;
  final bool isMe, focused;
  final DeviceStatus? otherSystem;
  final void Function(String) onOpen;

  @override
  Widget build(BuildContext context) {
    final report = status.report;
    final other = otherSystem?.report;
    final title = other == null
        ? status.title
        : status.fresh
            ? 'Running · ${status.title}'
            : 'Last reported · ${status.title}';
    final battery = status.fresh ? report.battery?.pct : null;
    return DecoratedBox(
      decoration: BoxDecoration(
        color: C.surface,
        borderRadius: BorderRadius.circular(10),
        border: Border.all(color: focused ? C.muted : C.line),
      ),
      child: Column(children: [
        Expanded(
          child: Padding(
            padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
            child: Row(children: [
              Icon(kindIcon(report.kind, report.name), size: 18, color: C.muted),
              const SizedBox(width: 8),
              Expanded(
                child: Column(
                  mainAxisAlignment: MainAxisAlignment.center,
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    Text(report.name, maxLines: 1, overflow: TextOverflow.ellipsis,
                        style: const TextStyle(fontSize: 14, fontWeight: FontWeight.w600)),
                    Text(title, maxLines: 1, overflow: TextOverflow.ellipsis,
                        style: TextStyle(fontSize: 11, color: C.of(status.level))),
                    Text('${report.os ?? _cardWhat(report)}${isMe ? ' · This device' : ''}',
                        maxLines: 1, overflow: TextOverflow.ellipsis,
                        style: const TextStyle(fontSize: 10, color: C.muted)),
                  ],
                ),
              ),
              if (battery != null && status.level == Level.ok)
                Text('${battery.round()}%', semanticsLabel: '${battery.round()} percent battery',
                    style: const TextStyle(fontSize: 12, color: C.muted)),
            ]),
          ),
        ),
        if (other != null)
          SizedBox(
            height: _otherSystemHeight,
            child: DecoratedBox(
              decoration: const BoxDecoration(border: Border(top: BorderSide(color: C.line))),
              child: TextButton(
                key: ValueKey('os:${other.id}'),
                onPressed: () => onOpen(other.id),
                style: TextButton.styleFrom(
                  foregroundColor: C.muted,
                  padding: const EdgeInsets.symmetric(horizontal: 10),
                ),
                child: Row(children: [
                  Icon(kindIcon(other.kind, other.name), size: 14),
                  const SizedBox(width: 8),
                  Expanded(
                    child: Column(
                      mainAxisAlignment: MainAxisAlignment.center,
                      crossAxisAlignment: CrossAxisAlignment.start,
                      children: [
                        Text(other.name, maxLines: 1, overflow: TextOverflow.ellipsis,
                            style: const TextStyle(fontSize: 12, fontWeight: FontWeight.w600)),
                        Text('${other.os ?? _cardWhat(other)} · ${status.fresh ? 'Not booted' : 'Other install'}',
                            maxLines: 1, overflow: TextOverflow.ellipsis,
                            style: const TextStyle(fontSize: 10)),
                      ],
                    ),
                  ),
                  const Icon(Icons.chevron_right, size: 14),
                ]),
              ),
            ),
          ),
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
    final sub = !r.isThing ? '${r.os ?? r.name} · not booted now' : problem || off ? s.title : head ?? s.title;
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
            child: Icon(r.isThing ? thingIcon(r.node?.type) : kindIcon(r.kind, r.name), size: 14, color: problem ? C.of(s.level) : C.text),
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
    case 'compose-project':
      return '${v('cp.running') ?? '?'} running · tap for containers';
    case 'container':
      return [v('ct.state'), if (v('ct.cpu') is num) '${v('ct.cpu')}% CPU', if (v('ct.mem') is num) '${(v('ct.mem') as num).round()} MB'].whereType<Object>().join(' · ');
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
  final bool? open; // null: nothing to unfold
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
            Container(width: 7, height: 7, decoration: BoxDecoration(shape: BoxShape.circle, color: problem ? C.of(level) : const Color(0xFF4FA36B))),
            const SizedBox(width: 6),
            Text(text, style: TextStyle(fontSize: 10.5, fontWeight: FontWeight.w500, color: problem ? C.of(level) : C.muted)),
            if (open != null) Icon(open! ? Icons.expand_less : Icons.expand_more, size: 13, color: C.muted) else const SizedBox(width: 2),
          ]),
        ),
      ),
    );
  }
}
