import 'report.dart';
import 'status.dart';

/// How things connect. Kept to a short fixed list so every reader agrees.
/// "dual-boot": another operating system on the same physical machine.
/// Only one runs at a time; they share the machine's disks and, if set up
/// that way, its storage and Compose projects.
const linkVias = {'usb', 'power', 'network', 'audio', 'thunderbolt', 'serial', 'bluetooth', 'dual-boot', 'docker'};

/// The direction a link carries what it carries, seen from the node that
/// declares it: "out" (from me to them, e.g. a UPS giving power), "in" (to
/// me), or "both".
const linkDirs = {'in', 'out', 'both'};

/// One declared connection from this node to another.
class NodeLink {
  NodeLink({required this.to, required this.via, required this.dir, this.detail});
  final String to;
  final String via;
  final String dir;
  final String? detail;

  static NodeLink? tryParse(Object? j) {
    if (j is! Map) return null;
    final to = j['to'], via = j['via'], dir = j['dir'], detail = j['detail'];
    if (to is! String || via is! String || dir is! String) return null;
    return NodeLink(to: to, via: via, dir: dir, detail: detail is String ? detail : null);
  }

  Map<String, Object?> toJson() => {'to': to, 'via': via, 'dir': dir, if (detail != null) 'detail': detail};
}

/// The "node" block every now.json carries: what it is, its place in the
/// hierarchy, and its declared connections in and out. See FORMAT.md.
class NodeDecl {
  NodeDecl({required this.id, required this.type, this.parent, this.children = const [], this.links = const []});
  final String id;

  /// "device" for a computer, phone or server; for an attached thing what it
  /// is, e.g. "ups", "audio-interface".
  final String type;
  final String? parent;
  final List<String> children;
  final List<NodeLink> links;

  static NodeDecl? tryParse(Object? j) {
    if (j is! Map) return null;
    final id = j['id'], type = j['type'], parent = j['parent'];
    if (id is! String || id.isEmpty || type is! String) return null;
    return NodeDecl(
      id: id,
      type: type,
      parent: parent is String ? parent : null,
      children: j['children'] is List ? [for (final c in j['children'] as List) if (c is String) c] : const [],
      links: j['links'] is List ? (j['links'] as List).map(NodeLink.tryParse).whereType<NodeLink>().toList() : const [],
    );
  }

  Map<String, Object?> toJson() => {
        'id': id,
        'type': type,
        'parent': parent,
        'children': children,
        'links': [for (final l in links) l.toJson()],
      };
}

/// A thing attached to a device (a UPS, an audio interface), as its host
/// wrote it into devices/<host>/things/<thing id>/.
class Thing {
  Thing(this.report, this.host, {this.params});
  final Report report;

  /// The device folder it was found in (its host when it was written).
  final String host;

  /// The optional params.json next to it: settings exported by the thing's
  /// own software (e.g. a mixer's levels), shown as they are.
  final Map<String, Object?>? params;

  String get id => report.id;
}

/// Checks every declaration against the others. Each problem is one plain
/// sentence; an empty list means the folder's hierarchy and links agree.
List<String> checkTopology(Map<String, Report> devices, Map<String, Thing> things) {
  final out = <String>[];
  final all = <String, Report>{...devices, for (final t in things.values) t.id: t.report};
  String name(String id) => all[id]?.name ?? id;
  for (final r in all.values) {
    final n = r.node;
    if (n == null) continue; // written before 2.5
    if (n.id != r.id) out.add('${r.name}: its node id "${n.id}" differs from its id "${r.id}".');
    if (r.isThing) {
      if (n.parent == null) {
        out.add('${r.name}: an attached thing must name its parent.');
      } else if (!all.containsKey(n.parent)) {
        out.add('${r.name}: its parent "${n.parent}" is not in this folder.');
      } else {
        final p = all[n.parent]!.node;
        if (p != null && !p.children.contains(r.id)) out.add('${r.name}: ${name(n.parent!)} does not list it as a child.');
      }
    }
    for (final c in n.children) {
      final child = all[c];
      if (child == null) {
        out.add('${r.name}: lists child "$c", which has no file.');
      } else if (child.node != null && child.node!.parent != r.id) {
        // A thing seen last on the other system of a dual-boot machine
        // names that system as parent; both are fine.
        final other = devices[child.node!.parent];
        if (other == null || other.machine == null || other.machine != devices[r.id]?.machine) {
          out.add('${r.name}: lists ${child.name} as a child, but it names ${name(child.node!.parent ?? '-')} as its parent.');
        }
      }
    }
    for (final l in n.links) {
      if (!all.containsKey(l.to)) out.add('${r.name}: links to "${l.to}", which has no file.');
      if (!linkVias.contains(l.via)) out.add('${r.name}: link to ${name(l.to)} is over "${l.via}", which is not one of ${linkVias.join(', ')}.');
      if (!linkDirs.contains(l.dir)) out.add('${r.name}: link to ${name(l.to)} has direction "${l.dir}" (in, out or both).');
    }
  }
  return out;
}

/// How an attached thing is doing, in the same shape as a device's status.
/// [hostName] is the name of the device it hangs off.
DeviceStatus thingStatus(Thing t, {String? hostName, DateTime? now}) {
  final r = t.report;
  final at = now ?? DateTime.now().toUtc();
  final age = at.difference(r.time);
  final host = hostName ?? t.host;
  final via = r.node?.links.map((l) => l.via).toSet().join(' and ') ?? '';
  if (r.connected == false) {
    return DeviceStatus(r, Level.inactive, 'Unplugged', '$host last saw it ${_ago(age)} ago. It stays on the map until you hide it.',
        fresh: false, age: age);
  }
  if (age > Duration(seconds: r.nextSeconds * 3 + 120)) {
    return DeviceStatus(r, Level.inactive, 'Not reported', '$host has not written about it for ${_ago(age)}; $host may be off or booted into another system.',
        fresh: false, age: age);
  }
  final bad = r.readings.where((x) => x.level == 'fault').firstOrNull ?? r.readings.where((x) => x.level == 'warn').firstOrNull;
  final level = bad == null ? Level.ok : bad.level == 'fault' ? Level.fault : Level.warn;
  Object? v(String k) => r.readings.where((x) => x.key == k).firstOrNull?.value;
  final type = r.node?.type;
  final title = bad != null
      ? '${bad.label ?? bad.key}: ${bad.value ?? 'unknown'}'
      : type == 'container'
          ? '${v('ct.state') ?? 'unknown'}'
          : type == 'compose-project'
              ? '${v('cp.running') ?? '?'} running'
              : 'Connected';
  final known = r.readings.where((x) => x.value != null).length;
  final where = type == 'container'
      ? 'A container in ${r.node?.parent?.replaceFirst(RegExp(r'^compose-[^-]+-'), '') ?? 'Docker'} on $host.'
      : type == 'compose-project'
          ? 'A Compose project in Docker on $host.'
          : 'Plugged into $host${via.isEmpty ? '' : ' ($via)'}.';
  return DeviceStatus(r, level, title, '$where $known of ${r.readings.length} readings available; the rest say why not.', fresh: true, age: age);
}

String _ago(Duration d) => d.inMinutes < 1
    ? '${d.inSeconds} s'
    : d.inHours < 1
        ? '${d.inMinutes} min'
        : d.inHours < 48
            ? '${d.inHours} h'
            : '${d.inDays} days';
