import 'compat.dart';
import 'constraints.dart';
import 'library.dart';
import 'model.dart';
import 'project.dart';

/// What's missing (roadmap row 27): a project's bill of materials, owned
/// against to-buy, plus the cables and adapters its connections still need,
/// required ports left unconnected, power shortfalls and open tasks.
///
/// Connections live in the project's `connections` list (FORMAT.md "What's
/// missing"):
/// `{"id", "from": {"placement", "port"}, "to": {"placement", "port"},
///   "required": true, "via": [{"item": id} | {"name", "a", "b", "passes"}]}`.
/// Ports come from the placed item's `ports` (`id`, `kind` = a connector
/// id, `dir`, `signals`, `power`, `required`).
class BomLine {
  BomLine(this.name, this.status, {this.qty = 1, this.why, this.item, this.placement, this.from, this.to, this.want});
  final String name;

  /// For a `missing` cable or adapter: the two ports it must join, and the
  /// signal wanted through it, so discovery (row 33) can search and check.
  final Port? from, to;
  final String? want;

  /// `owned` (an owned or installed item, or a live thing), `to-buy`
  /// (planned or only proposed), `missing` (nothing chosen yet: a cable or
  /// adapter a connection needs).
  final String status;
  int qty;
  final String? why, item, placement;
  @override
  String toString() => '$qty x $name: $status${why == null ? '' : ' ($why)'}';
}

class ProjectNeeds {
  ProjectNeeds(this.lines, this.unconnected, this.connectionProblems, this.physical, this.openTasks);
  final List<BomLine> lines;

  /// "Server: port eth1 is required and not connected".
  final List<String> unconnected;

  /// Per connection id, why it won't work as drawn.
  final Map<String, List<String>> connectionProblems;

  /// Failing or unknown physical findings (power shortfall, fit, heat).
  final List<Finding> physical;
  final List<String> openTasks;

  List<BomLine> get toBuy => [for (final l in lines) if (l.status != 'owned') l];
}

Port? _port(Item? it, String placementName, String? portId) {
  if (it == null || portId == null) return null;
  for (final p in it.ports) {
    if (p['id'] == portId && p['kind'] is String) {
      final pw = p['power'];
      return Port('$placementName ${p['label'] ?? portId}', p['kind'] as String,
          gender: p['gender'] as String?,
          dir: (p['dir'] as String?) ?? 'both',
          signals: p['signals'] is List ? [for (final s in p['signals'] as List) '$s'] : null,
          power: pw is Map ? {for (final e in pw.entries) if (e.value is num || e.value is String) '${e.key}': e.value as Object} : null);
    }
  }
  return null;
}

ProjectNeeds projectNeeds(Project project, Map<String, Item> items, {Library? library}) {
  final lib = library ?? Library.builtIn;
  final lines = <BomLine>[];
  final placements = <String, Placement>{};
  void add(BomLine l) {
    final same = lines.where((x) => x.name == l.name && x.status == l.status && x.item == null && l.item == null).firstOrNull;
    if (same != null) {
      same.qty += l.qty;
    } else {
      lines.add(l);
    }
  }

  for (final a in project.assemblies) {
    for (final p in a.placements) {
      placements[p.id] = p;
      final it = p.item == null ? null : items[p.item];
      final status = p.thing != null || (it != null && (it.state == 'owned' || it.state == 'installed')) ? 'owned' : 'to-buy';
      lines.add(BomLine(it?.name ?? p.name, status, item: p.item, placement: p.id, why: status == 'to-buy' ? (it == null ? 'planned' : 'proposed, not owned yet') : null));
    }
  }

  final used = <String>{};
  final problems = <String, List<String>>{};
  final conns = project.extra['connections'];
  for (final c in conns is List ? conns.whereType<Map>() : const <Map>[]) {
    final id = '${c['id'] ?? '?'}';
    final f = c['from'], t = c['to'];
    if (f is! Map || t is! Map) continue;
    final fp = placements[f['placement']], tp = placements[t['placement']];
    used..add('${f['placement']}/${f['port']}')..add('${t['placement']}/${t['port']}');
    final a = _port(fp == null ? null : items[fp.item], fp?.name ?? '?', f['port'] as String?);
    final b = _port(tp == null ? null : items[tp.item], tp?.name ?? '?', t['port'] as String?);
    if (a == null || b == null) {
      problems[id] = ['${a == null ? (fp?.name ?? f['placement']) : (tp?.name ?? t['placement'])} has no port ${a == null ? f['port'] : t['port']} declared'];
      continue;
    }
    final via = c['via'] is List ? (c['via'] as List).whereType<Map>().toList() : <Map>[];
    if (via.isEmpty) {
      // Nothing chosen: the cable it needs, by the two ends' shapes.
      final ka = lib.connectors[a.connector]?.label ?? a.connector, kb = lib.connectors[b.connector]?.label ?? b.connector;
      add(BomLine(ka == kb ? '$ka cable' : '$ka to $kb cable', 'missing', why: 'for ${a.name} to ${b.name}', from: a, to: b, want: c['want'] as String?));
      continue;
    }
    final links = <Link>[];
    for (final v in via) {
      final it = v['item'] is String ? items[v['item']] : null;
      final ends = it?.ports.map((p) => p['kind']).whereType<String>().toList() ?? const <String>[];
      final la = (v['a'] as String?) ?? (ends.isNotEmpty ? ends.first : null), lb = (v['b'] as String?) ?? (ends.length > 1 ? ends[1] : la);
      if (la == null || lb == null) continue;
      final passes = v['passes'] is List ? [for (final s in v['passes'] as List) '$s'] : null;
      links.add(Link('${it?.name ?? v['name'] ?? 'cable'}', la, lb, passes: passes));
      if (it == null) {
        add(BomLine('${v['name'] ?? 'cable'}', 'to-buy', why: 'for ${a.name} to ${b.name}'));
      } else {
        lines.add(BomLine(it.name, it.state == 'owned' || it.state == 'installed' ? 'owned' : 'to-buy', item: it.id));
      }
    }
    final verdict = checkChain(a, links, b, want: c['want'] as String?, library: lib);
    if (verdict.verdict == 'fail') {
      problems[id] = [for (final l in verdict.layers) if (l.verdict == 'fail') ...l.reasons];
      if (verdict['physical'].verdict == 'fail') add(BomLine('adapter', 'missing', why: verdict['physical'].reasons.first, from: a, to: b, want: c['want'] as String?));
    }
  }

  final unconnected = <String>[];
  for (final p in placements.values) {
    final it = p.item == null ? null : items[p.item];
    for (final port in it?.ports ?? const <Map<String, Object?>>[]) {
      if (port['required'] == true && !used.contains('${p.id}/${port['id']}')) unconnected.add('${p.name}: ${port['label'] ?? port['id']} is required and not connected');
    }
  }

  final physical = [
    // Rack units, overlaps, weight and power totals (row 6), then row 25.
    for (final a in project.assemblies)
      for (final t in a.check().problems) Finding(t.startsWith('Power') ? 'power' : (t.startsWith('Weight') ? 'weight' : 'fit'), 'fail', 'verified', t),
    for (final a in project.assemblies)
      for (final f in checkPhysical(a))
        if (f.level == 'fail' || f.level == 'unknown') f,
  ];
  return ProjectNeeds(lines, unconnected, problems, physical, [for (final t in project.tasks) if (!t.done) t.text]);
}
