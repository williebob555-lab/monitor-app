import 'dart:convert';
import 'dart:io';
import 'dart:math';

/// The shared model (row 5 of docs/ROADMAP.md, FORMAT.md "Model"): one file
/// per object anyone owns, plans or observes, written from any device.
///
///   model/items/<id>/item.json              the item now
///   model/items/<id>/history/<device>.jsonl every change, one line each,
///                                           appended only by that device
///
/// Any device may change any item, so two devices can write item.json at
/// once and Syncthing keeps the loser as item.sync-conflict-*.json. Readers
/// take the newest `t` of all copies (then the higher `rev`, then `by`);
/// [ModelStore.resolveConflicts] makes that copy item.json, notes each loser
/// in history and deletes the copy. Nothing is lost: every version ever
/// written is in some device's history file, which only that device
/// appends to, so history files never conflict.
const modelDirName = 'model';
const itemStates = ['proposed', 'owned', 'installed', 'retired'];

/// Ids are file names: lowercase letters, digits, '-', '_' and '.', not
/// starting with a dot, at most 64. Anything else is refused, so an id can
/// never reach outside its folder.
final _idRe = RegExp(r'^[a-z0-9_-][a-z0-9._-]{0,63}$');
bool validModelId(String id) => _idRe.hasMatch(id) && !id.contains('..');

String newModelId([Random? r]) {
  final rnd = r ?? Random.secure();
  return List.generate(12, (_) => rnd.nextInt(16).toRadixString(16)).join();
}

/// One value with where it came from: measured, declared (spec or user),
/// estimated or unknown (FORMAT.md "Built to grow").
class Prop {
  Prop(this.value, {this.unit, this.src = 'declared'});
  final Object? value;
  final String? unit;
  final String src;

  static Prop? fromJson(Object? j) {
    if (j is! Map) return j == null ? null : Prop(j);
    return Prop(j['value'], unit: j['unit'] as String?, src: (j['src'] as String?) ?? 'declared');
  }

  Map<String, Object?> toJson() => {'value': value, if (unit != null) 'unit': unit, 'src': src};
}

/// An object in the model. Unknown keys are kept and written back, so a
/// newer app's fields survive an older one's edit.
/// What a store needs from a record: identity and the write stamp.
abstract class Versioned {
  String get id;
  int get rev;
  set rev(int v);
  String? get by;
  set by(String? v);
  DateTime? get time;
  set time(DateTime? v);
  String get state;
  Map<String, Object?> toJson();
}

/// Newest wins: later t, then higher rev, then the larger writer id (so
/// every device picks the same one).
int newerVersion(Versioned a, Versioned b) {
  final ta = a.time?.millisecondsSinceEpoch ?? 0, tb = b.time?.millisecondsSinceEpoch ?? 0;
  if (ta != tb) return ta.compareTo(tb);
  if (a.rev != b.rev) return a.rev.compareTo(b.rev);
  return (a.by ?? '').compareTo(b.by ?? '');
}

class Item implements Versioned {
  Item({
    required this.id,
    required this.type,
    required this.name,
    this.state = 'proposed',
    this.parent,
    Map<String, Prop>? props,
    List<Map<String, Object?>>? ports,
    List<Map<String, Object?>>? links,
    this.observes,
    this.rev = 0,
    this.by,
    this.time,
    Map<String, Object?>? extra,
  })  : props = props ?? {},
        ports = ports ?? [],
        links = links ?? [],
        extra = extra ?? {};

  @override
  final String id;
  String type;
  String name;
  @override
  String state;
  String? parent;
  final Map<String, Prop> props;

  /// Each port: {"id", "kind" (e.g. "usb-c"), "dir": in|out|both, ...}.
  final List<Map<String, Object?>> ports;

  /// Each link: {"to": item id, "port", "toPort", "via", ...}.
  final List<Map<String, Object?>> links;

  /// The device or thing id in devices/ this item is (its live telemetry).
  String? observes;
  @override
  int rev;
  @override
  String? by;
  @override
  DateTime? time;
  final Map<String, Object?> extra;

  static const _known = {'v', 'id', 'type', 'name', 'state', 'parent', 'props', 'ports', 'links', 'observes', 'rev', 'by', 't'};

  static Item? fromJson(Object? j) {
    if (j is! Map) return null;
    final id = j['id'], type = j['type'], name = j['name'];
    if (id is! String || !validModelId(id) || type is! String || name is! String) return null;
    final props = <String, Prop>{};
    if (j['props'] is Map) {
      for (final e in (j['props'] as Map).entries) {
        final p = Prop.fromJson(e.value);
        if (e.key is String && p != null) props[e.key as String] = p;
      }
    }
    List<Map<String, Object?>> maps(Object? l) => [if (l is List) for (final x in l) if (x is Map) x.cast<String, Object?>()];
    final t = j['t'];
    return Item(
      id: id,
      type: type,
      name: name,
      state: itemStates.contains(j['state']) ? j['state'] as String : 'proposed',
      parent: j['parent'] is String ? j['parent'] as String : null,
      props: props,
      ports: maps(j['ports']),
      links: maps(j['links']),
      observes: j['observes'] is String ? j['observes'] as String : null,
      rev: (j['rev'] as num?)?.toInt() ?? 0,
      by: j['by'] is String ? j['by'] as String : null,
      time: t is num ? DateTime.fromMillisecondsSinceEpoch((t * 1000).round(), isUtc: true) : null,
      extra: {for (final e in j.entries) if (e.key is String && !_known.contains(e.key)) e.key as String: e.value},
    );
  }

  @override
  Map<String, Object?> toJson() => {
        ...extra,
        'v': 1,
        'id': id,
        'type': type,
        'name': name,
        'state': state,
        'parent': parent,
        'props': {for (final e in props.entries) e.key: e.value.toJson()},
        'ports': ports,
        'links': links,
        if (observes != null) 'observes': observes,
        'rev': rev,
        if (by != null) 'by': by,
        if (time != null) 't': time!.millisecondsSinceEpoch / 1000,
      };

  Item copy() => Item.fromJson(jsonDecode(jsonEncode(toJson())))!;

  static int newer(Item a, Item b) => newerVersion(a, b);
}

/// One folder of versioned records (`model/<collection>/<id>/<file>.json`
/// plus per-device history). [ModelStore] holds items; [ProjectStore]
/// (project.dart) projects.
class VersionedStore<T extends Versioned> {
  VersionedStore(this.root, this.deviceId, {required this.collection, required this.fileBase, required this.parse, this.validate});

  final String collection;

  /// The record's file name without .json ("item", "project").
  final String fileBase;
  final T? Function(Object? json) parse;

  /// Extra checks before a write; throw ArgumentError to refuse it.
  final void Function(T record)? validate;


  /// The shared Monitor folder.
  final String root;

  /// This device's id: written as `by` and names its history file.
  final String deviceId;

  static final _sep = Platform.pathSeparator;
  String get itemsPath => '$root$_sep$modelDirName$_sep$collection';
  String _dir(String id) => '$itemsPath$_sep$id';

  /// item.json and any conflict copies of it, parsed (unreadable ones left out).
  List<(File, T)> _copies(String id) {
    final d = Directory(_dir(id));
    if (!d.existsSync()) return const [];
    final out = <(File, T)>[];
    for (final f in d.listSync()) {
      if (f is! File) continue;
      final n = f.uri.pathSegments.last;
      if (n != '$fileBase.json' && !(n.startsWith('$fileBase.sync-conflict-') && n.endsWith('.json'))) continue;
      try {
        final it = parse(jsonDecode(f.readAsStringSync()));
        if (it != null && it.id == id) out.add((f, it));
      } catch (_) {}
    }
    return out;
  }

  T? get(String id) {
    if (!validModelId(id)) return null;
    final c = _copies(id);
    if (c.isEmpty) return null;
    c.sort((a, b) => newerVersion(b.$2, a.$2));
    return c.first.$2;
  }

  List<String> ids() {
    final d = Directory(itemsPath);
    if (!d.existsSync()) return const [];
    return [
      for (final e in d.listSync())
        if (e is Directory && validModelId(e.uri.pathSegments.where((s) => s.isNotEmpty).last)) e.uri.pathSegments.where((s) => s.isNotEmpty).last
    ]..sort();
  }

  /// Every item, retired ones only if [withRetired].
  List<T> list({bool withRetired = false}) => [
        for (final id in ids())
          if (get(id) case final it? when withRetired || it.state != 'retired') it
      ];

  /// Writes [item] as the next revision, by this device, now. Returns what
  /// was written.
  T put(T item, {DateTime? now}) {
    if (!validModelId(item.id)) throw ArgumentError('invalid id ${item.id}');
    validate?.call(item);
    final old = get(item.id);
    final it = parse(jsonDecode(jsonEncode(item.toJson())))!
      ..rev = (old?.rev ?? 0) + 1
      ..by = deviceId
      ..time = (now ?? DateTime.now()).toUtc();
    if (old != null && it.time!.isBefore(old.time ?? it.time!)) {
      // This device's clock is behind the last writer's: stay after it,
      // or everyone else would keep the older version.
      it.time = old.time!.add(const Duration(milliseconds: 1));
    }
    final d = Directory(_dir(it.id))..createSync(recursive: true);
    final tmp = File('${d.path}$_sep.syncthing.$fileBase.json.tmp');
    tmp.writeAsStringSync(const JsonEncoder.withIndent('  ').convert(it.toJson()), flush: true);
    tmp.renameSync('${d.path}$_sep$fileBase.json');
    _history(it.id, {'t': it.time!.millisecondsSinceEpoch / 1000, 'by': deviceId, 'rev': it.rev, 'item': it.toJson()});
    return it;
  }


  void _history(String id, Map<String, Object?> line) {
    final h = Directory('${_dir(id)}${_sep}history')..createSync(recursive: true);
    File('${h.path}$_sep$deviceId.jsonl').writeAsStringSync('${jsonEncode(line)}\n', mode: FileMode.append, flush: true);
  }

  /// Every recorded version of [id], oldest first, from all devices.
  List<T> history(String id) {
    if (!validModelId(id)) return const [];
    final h = Directory('${_dir(id)}${_sep}history');
    if (!h.existsSync()) return const [];
    final out = <T>[];
    for (final f in h.listSync()) {
      if (f is! File || !f.path.endsWith('.jsonl') || f.path.contains('.sync-conflict-')) continue;
      for (final l in const LineSplitter().convert(f.readAsStringSync())) {
        try {
          final j = jsonDecode(l);
          if (j is Map && j['conflictLoser'] != true) {
            final it = parse(j['item']);
            if (it != null) out.add(it);
          }
        } catch (_) {}
      }
    }
    out.sort(newerVersion);
    return out;
  }

  /// The model as it was at [t]: each item's newest version written at or
  /// before then (items created later are left out).
  List<T> asOf(DateTime t, {bool withRetired = false}) {
    final out = <T>[];
    for (final id in ids()) {
      T? best;
      for (final it in history(id)) {
        if (it.time != null && !it.time!.isAfter(t)) best = it;
      }
      if (best != null && (withRetired || best.state != 'retired')) out.add(best);
    }
    return out;
  }

  /// Conflict copies left by Syncthing, for Folder health.
  List<String> conflicts() => [
        for (final id in ids())
          for (final (f, _) in _copies(id))
            if (f.uri.pathSegments.last != '$fileBase.json') f.path
      ];

  /// Makes the newest copy item.json and deletes the others, noting each
  /// loser in this device's history. Returns how many copies were removed.
  int resolveConflicts() {
    var n = 0;
    for (final id in ids()) {
      final c = _copies(id);
      if (c.length < 2) continue;
      c.sort((a, b) => newerVersion(b.$2, a.$2));
      final win = c.first;
      final main = File('${_dir(id)}$_sep$fileBase.json');
      if (win.$1.path != main.path) {
        final tmp = File('${_dir(id)}$_sep.syncthing.$fileBase.json.tmp');
        tmp.writeAsStringSync(const JsonEncoder.withIndent('  ').convert(win.$2.toJson()), flush: true);
        tmp.renameSync(main.path);
      }
      for (final (f, it) in c.skip(1)) {
        _history(id, {'t': DateTime.now().toUtc().millisecondsSinceEpoch / 1000, 'by': deviceId, 'conflictLoser': true, 'item': it.toJson()});
        if (f.path == main.path) continue; // already replaced by the winner
        try {
          f.deleteSync();
          n++;
        } catch (_) {}
      }
      if (win.$1.path != main.path) {
        try {
          win.$1.deleteSync();
          n++;
        } catch (_) {}
      }
    }
    return n;
  }
}

/// Items: `model/items/<id>/item.json`.
class ModelStore extends VersionedStore<Item> {
  ModelStore(String root, String deviceId)
      : super(root, deviceId, collection: 'items', fileBase: 'item', parse: Item.fromJson, validate: (it) {
          if (!itemStates.contains(it.state)) throw ArgumentError('invalid state ${it.state}');
        });

  /// Items are never deleted: they are retired, and stay in history.
  Item? retire(String id, {DateTime? now}) {
    final it = get(id);
    if (it == null) return null;
    return put(it..state = 'retired', now: now);
  }
}
