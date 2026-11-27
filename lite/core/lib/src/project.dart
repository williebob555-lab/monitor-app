import 'model.dart';

/// Projects and assemblies (row 6 of docs/ROADMAP.md, FORMAT.md "Projects"):
/// `model/projects/<id>/project.json`, versioned like items.
///
/// A project has a goal, the items it involves, tasks, and assemblies: a
/// rack (or any frame) with typed placements. A placement points at a model
/// item, a live thing id from devices/, or neither (a planned part with just
/// a name), and says where it sits: rack units, face, and dimensions with
/// units.
const projectStates = ['draft', 'active', 'done', 'archived'];

/// A number with a unit and where it came from, e.g. depth 450 mm declared.
class Qty {
  Qty(this.value, this.unit, {this.src = 'declared'});
  final double value;
  final String unit;
  final String src;

  static Qty? fromJson(Object? j) {
    if (j is! Map || j['value'] is! num || j['unit'] is! String) return null;
    return Qty((j['value'] as num).toDouble(), j['unit'] as String, src: (j['src'] as String?) ?? 'declared');
  }

  Map<String, Object?> toJson() => {'value': value, 'unit': unit, 'src': src};

  static const _toBase = {
    // length -> mm
    'mm': ('length', 1.0), 'cm': ('length', 10.0), 'm': ('length', 1000.0), 'in': ('length', 25.4), 'ft': ('length', 304.8),
    // mass -> kg
    'g': ('mass', 0.001), 'kg': ('mass', 1.0), 'lb': ('mass', 0.45359237), 'oz': ('mass', 0.028349523125),
    // power -> W
    'W': ('power', 1.0), 'kW': ('power', 1000.0), 'mW': ('power', 0.001),
  };

  /// In the base unit of its kind (mm, kg, W), or null for an unknown unit
  /// or a different kind than [kind].
  double? base(String kind) {
    final b = _toBase[unit];
    return b == null || b.$1 != kind ? null : value * b.$2;
  }
}

class Placement {
  Placement({required this.id, required this.name, this.item, this.thing, this.u, this.height, this.face = 'front', this.fullDepth = false, this.depth, this.weight, this.power, Map<String, Object?>? extra}) : extra = extra ?? {};
  final String id;
  String name;

  /// A model item id, a live thing/device id, or neither.
  String? item, thing;

  /// Rack units it occupies, bottom first: (1, 2) is U1-U2. null: not placed yet.
  (int, int)? u;

  /// Rack units it takes (2 for a 2U amplifier), known before it is placed.
  /// When placed, [units] is this, or else the size of [u].
  int? height;
  int? get units => height ?? (u == null ? null : u!.$2 - u!.$1 + 1);

  /// "front" or "rear". [fullDepth] blocks both faces.
  String face;
  bool fullDepth;
  Qty? depth, weight, power;
  final Map<String, Object?> extra;

  static const _known = {'id', 'name', 'item', 'thing', 'u', 'height', 'face', 'fullDepth', 'depth', 'weight', 'power'};

  static Placement? fromJson(Object? j) {
    if (j is! Map || j['id'] is! String || j['name'] is! String) return null;
    final u = j['u'];
    return Placement(
      id: j['id'] as String,
      name: j['name'] as String,
      item: j['item'] as String?,
      thing: j['thing'] as String?,
      u: u is List && u.length == 2 && u[0] is int && u[1] is int ? (u[0] as int, u[1] as int) : null,
      height: j['height'] is int && (j['height'] as int) > 0 ? j['height'] as int : null,
      face: j['face'] == 'rear' ? 'rear' : 'front',
      fullDepth: j['fullDepth'] == true,
      depth: Qty.fromJson(j['depth']),
      weight: Qty.fromJson(j['weight']),
      power: Qty.fromJson(j['power']),
      extra: {for (final e in j.entries) if (e.key is String && !_known.contains(e.key)) e.key as String: e.value},
    );
  }

  Map<String, Object?> toJson() => {
        ...extra,
        'id': id,
        'name': name,
        if (item != null) 'item': item,
        if (thing != null) 'thing': thing,
        'u': u == null ? null : [u!.$1, u!.$2],
        if (height != null) 'height': height,
        'face': face,
        if (fullDepth) 'fullDepth': true,
        if (depth != null) 'depth': depth!.toJson(),
        if (weight != null) 'weight': weight!.toJson(),
        if (power != null) 'power': power!.toJson(),
      };
}

class Assembly {
  Assembly({required this.id, required this.name, this.type = 'rack', this.units, this.depth, this.maxWeight, this.maxPower, List<Placement>? placements, Map<String, Object?>? extra})
      : placements = placements ?? [],
        extra = extra ?? {};
  final String id;
  String name;
  String type;

  /// Height in rack units.
  int? units;
  Qty? depth, maxWeight, maxPower;
  final List<Placement> placements;
  final Map<String, Object?> extra;

  static const _known = {'id', 'name', 'type', 'units', 'depth', 'maxWeight', 'maxPower', 'placements'};

  static Assembly? fromJson(Object? j) {
    if (j is! Map || j['id'] is! String || j['name'] is! String) return null;
    return Assembly(
      id: j['id'] as String,
      name: j['name'] as String,
      type: (j['type'] as String?) ?? 'rack',
      units: j['units'] is int ? j['units'] as int : null,
      depth: Qty.fromJson(j['depth']),
      maxWeight: Qty.fromJson(j['maxWeight']),
      maxPower: Qty.fromJson(j['maxPower']),
      placements: [if (j['placements'] is List) for (final p in j['placements'] as List) if (Placement.fromJson(p) case final x?) x],
      extra: {for (final e in j.entries) if (e.key is String && !_known.contains(e.key)) e.key as String: e.value},
    );
  }

  Map<String, Object?> toJson() => {
        ...extra,
        'id': id,
        'name': name,
        'type': type,
        if (units != null) 'units': units,
        if (depth != null) 'depth': depth!.toJson(),
        if (maxWeight != null) 'maxWeight': maxWeight!.toJson(),
        if (maxPower != null) 'maxPower': maxPower!.toJson(),
        'placements': [for (final p in placements) p.toJson()],
      };

  /// What is wrong with the layout, in words, plus totals with coverage.
  AssemblyCheck check() {
    final problems = <String>[];
    final placed = placements.where((p) => p.u != null).toList();
    for (final p in placed) {
      final (a, b) = p.u!;
      if (a < 1 || b < a) {
        problems.add('${p.name}: U$a-U$b is not a valid range');
      } else if (units != null && b > units!) {
        problems.add('${p.name}: U$a-U$b is outside the ${units}U ${type}');
      } else if (p.height != null && b - a + 1 != p.height) {
        problems.add('${p.name} is ${p.height}U but placed at U$a-U$b');
      }
      final pd = p.depth?.base('length'), rd = depth?.base('length');
      if (pd != null && rd != null && pd > rd) {
        problems.add('${p.name}: ${_n(pd)} mm deep, the $type is ${_n(rd)} mm');
      }
    }
    for (var i = 0; i < placed.length; i++) {
      for (var j = i + 1; j < placed.length; j++) {
        final x = placed[i], y = placed[j];
        final sameSide = x.fullDepth || y.fullDepth || x.face == y.face;
        if (sameSide && x.u!.$1 <= y.u!.$2 && y.u!.$1 <= x.u!.$2) {
          problems.add('${x.name} and ${y.name} overlap at U${x.u!.$1 > y.u!.$1 ? x.u!.$1 : y.u!.$1}');
        }
      }
    }
    Total total(Qty? Function(Placement) f, String kind, Qty? limit) {
      var sum = 0.0, known = 0;
      for (final p in placements) {
        final v = f(p)?.base(kind);
        if (v == null) continue;
        sum += v;
        known++;
      }
      final t = Total(sum, known, placements.length, limit?.base(kind));
      if (t.limit != null && t.sum > t.limit!) {
        problems.add('${kind == 'mass' ? 'Weight' : 'Power'} ${_n(t.sum)} ${kind == 'mass' ? 'kg' : 'W'} is over the ${_n(t.limit!)} ${kind == 'mass' ? 'kg' : 'W'} limit${t.complete ? '' : ' (and ${t.count - t.known} parts have no value yet)'}');
      }
      return t;
    }

    final w = total((p) => p.weight, 'mass', maxWeight);
    final pw = total((p) => p.power, 'power', maxPower);
    final used = <int>{
      for (final p in placed)
        if (p.u!.$1 >= 1 && p.u!.$2 >= p.u!.$1)
          for (var k = p.u!.$1; k <= p.u!.$2; k++) k
    };
    return AssemblyCheck(problems, w, pw, units == null ? null : units! - used.where((k) => k <= units!).length);
  }
}

String _n(double v) => v == v.roundToDouble() ? v.round().toString() : v.toStringAsFixed(1);

/// A sum over placements, and how many of them had a value: "120 W from 3
/// of 5 parts" is not "120 W".
class Total {
  Total(this.sum, this.known, this.count, this.limit);
  final double sum;
  final int known, count;
  final double? limit;
  bool get complete => known == count;
}

class AssemblyCheck {
  AssemblyCheck(this.problems, this.weightKg, this.powerW, this.freeUnits);
  final List<String> problems;
  final Total weightKg, powerW;

  /// Unused rack units on either face, or null without a height.
  final int? freeUnits;
  bool get ok => problems.isEmpty;
}

class Task {
  Task(this.id, this.text, {this.done = false});
  final String id;
  String text;
  bool done;
  Map<String, Object?> toJson() => {'id': id, 'text': text, 'done': done};
}

class Project implements Versioned {
  Project({required this.id, required this.name, this.goal = '', this.state = 'draft', List<String>? items, List<Task>? tasks, List<Assembly>? assemblies, this.rev = 0, this.by, this.time, Map<String, Object?>? extra})
      : items = items ?? [],
        tasks = tasks ?? [],
        assemblies = assemblies ?? [],
        extra = extra ?? {};

  @override
  final String id;
  String name, goal;
  @override
  String state;

  /// Model item ids this project involves.
  final List<String> items;
  final List<Task> tasks;
  final List<Assembly> assemblies;
  @override
  int rev;
  @override
  String? by;
  @override
  DateTime? time;
  final Map<String, Object?> extra;

  static const _known = {'v', 'id', 'name', 'goal', 'state', 'items', 'tasks', 'assemblies', 'rev', 'by', 't'};

  static Project? fromJson(Object? j) {
    if (j is! Map || j['id'] is! String || !validModelId(j['id'] as String) || j['name'] is! String) return null;
    final t = j['t'];
    return Project(
      id: j['id'] as String,
      name: j['name'] as String,
      goal: (j['goal'] as String?) ?? '',
      state: projectStates.contains(j['state']) ? j['state'] as String : 'draft',
      items: [if (j['items'] is List) for (final x in j['items'] as List) if (x is String) x],
      tasks: [
        if (j['tasks'] is List)
          for (final x in j['tasks'] as List)
            if (x is Map && x['id'] is String && x['text'] is String) Task(x['id'] as String, x['text'] as String, done: x['done'] == true)
      ],
      assemblies: [if (j['assemblies'] is List) for (final a in j['assemblies'] as List) if (Assembly.fromJson(a) case final x?) x],
      rev: (j['rev'] as num?)?.toInt() ?? 0,
      by: j['by'] as String?,
      time: t is num ? DateTime.fromMillisecondsSinceEpoch((t * 1000).round(), isUtc: true) : null,
      extra: {for (final e in j.entries) if (e.key is String && !_known.contains(e.key)) e.key as String: e.value},
    );
  }

  @override
  Map<String, Object?> toJson() => {
        ...extra,
        'v': 1,
        'id': id,
        'name': name,
        'goal': goal,
        'state': state,
        'items': items,
        'tasks': [for (final t in tasks) t.toJson()],
        'assemblies': [for (final a in assemblies) a.toJson()],
        'rev': rev,
        if (by != null) 'by': by,
        if (time != null) 't': time!.millisecondsSinceEpoch / 1000,
      };
}

/// Projects: `model/projects/<id>/project.json`, with the same history,
/// conflict and time-travel rules as items.
class ProjectStore extends VersionedStore<Project> {
  ProjectStore(String root, String deviceId)
      : super(root, deviceId, collection: 'projects', fileBase: 'project', parse: Project.fromJson, validate: (p) {
          if (!projectStates.contains(p.state)) throw ArgumentError('invalid state ${p.state}');
        });

  /// Projects are archived, never deleted.
  Project? archive(String id, {DateTime? now}) {
    final p = get(id);
    if (p == null) return null;
    return put(p..state = 'archived', now: now);
  }

  @override
  List<Project> list({bool withRetired = false}) => [
        for (final p in super.list(withRetired: true))
          if (withRetired || p.state != 'archived') p
      ];
}
