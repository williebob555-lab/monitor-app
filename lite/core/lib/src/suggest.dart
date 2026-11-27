import 'model.dart';
import 'report.dart';
import 'status.dart' show freshLimit;
import 'topology.dart' show Thing;

/// Observed to modeled (roadmap row 22): every device and thing seen in
/// `devices/` offered as an inventory item linked to its live files
/// (`observes`). An accepted item also keeps a `match` key, the thing's
/// lasting identity (USB id and serial, the machine of a dual-boot pair, a
/// container's name), so when its telemetry id changes (a UPS without a
/// serial moved to another USB port) the item is offered a relink instead
/// of a duplicate.
class LinkSuggestion {
  LinkSuggestion({required this.match, required this.observes, required this.type, required this.name, this.also = const [], this.parentObserves, this.props = const {}, this.relinkItem});

  /// Lasting identity, stored on the item as `match`.
  final String match;

  /// The device or thing id to link (the freshest system of a dual-boot
  /// machine).
  final String observes;

  /// Other ids that are the same object (the other system of a dual boot).
  final List<String> also;
  final String type, name;

  /// The host's id when this is a thing, so the item can sit under the
  /// host's item.
  final String? parentObserves;
  final Map<String, Prop> props;

  /// Set when an existing item lost its link and this is the same object
  /// under a new id: accepting moves that item's `observes`.
  final String? relinkItem;
}

String? _reading(Report r, String key) {
  for (final x in r.readings) {
    if (x.key == key && x.value != null) return '${x.value}';
  }
  return null;
}

/// The lasting identity of a thing.
String thingMatch(Thing t) {
  final r = t.report;
  final usb = _reading(r, 'usb.id'), serial = _reading(r, 'usb.serial') ?? _reading(r, 'ups.serial');
  if (usb != null) return serial != null ? 'usb:$usb:$serial' : 'usb:$usb@${t.host}:${_reading(r, 'usb.port') ?? t.id}';
  final ident = _reading(r, 'ups.identity');
  final m = ident == null ? null : RegExp(r'USB ([0-9a-f]{4}:[0-9a-f]{4}), serial (.+)$').firstMatch(ident);
  if (m != null) return 'usb:${m[1]}:${m[2]}';
  return 'thing:${t.id}';
}

/// Every id an item stands for: `observes` and, for a dual-boot machine,
/// `alsoObserves`.
List<String> _observed(Item i) => [if (i.observes != null) i.observes!, if (i.extra['alsoObserves'] is List) for (final x in i.extra['alsoObserves'] as List) '$x'];

String _deviceType(Report r) => switch (r.kind) { 'android' => 'mobile', 'windows' || 'linux' => 'computer', _ => r.kind };

/// What could become (or be relinked to) an inventory item. [items] are the
/// current items (retired ones are ignored).
List<LinkSuggestion> suggestLinks(Map<String, Report> reports, Map<String, Thing> things, List<Item> items, DateTime now) {
  final live = items.where((i) => i.state != 'retired').toList();
  final byObs = {for (final i in live) for (final o in _observed(i)) o: i};
  final byMatch = {for (final i in live) if (i.extra['match'] is String) i.extra['match'] as String: i};
  final out = <LinkSuggestion>[];

  // Devices, one per physical machine.
  final groups = <String, List<Report>>{};
  for (final r in reports.values) {
    if (r.isThing) continue;
    final key = r.machine != null && r.kind != 'android' ? 'machine:${r.machine}' : 'device:${r.id}';
    (groups[key] ??= []).add(r);
  }
  for (final e in groups.entries) {
    final rs = e.value..sort((a, b) => b.time.compareTo(a.time));
    if (byMatch.containsKey(e.key) || rs.any((r) => byObs.containsKey(r.id))) continue;
    final names = [for (final r in rs) r.name].toSet().toList();
    out.add(LinkSuggestion(
      match: e.key,
      observes: rs.first.id,
      also: [for (final r in rs.skip(1)) r.id],
      type: _deviceType(rs.first),
      name: names.length == 1 ? names.first : '${names.first} (also ${names.skip(1).join(', ')})',
      props: {if (rs.first.os != null) 'os': Prop(rs.first.os, src: 'measured')},
    ));
  }

  // Things; one entry per lasting identity (both systems of a dual boot
  // write the same UPS), the most recently written first.
  final seen = <String>{};
  final ts = things.values.toList()..sort((a, b) => b.report.time.compareTo(a.report.time));
  for (final t in ts) {
    if (t.report.connected == false && now.difference(t.report.time) > freshLimit(t.report)) continue;
    final match = thingMatch(t);
    if (!seen.add(match)) continue;
    if (byObs.containsKey(t.id)) continue;
    final had = byMatch[match];
    final r = t.report;
    final props = <String, Prop>{
      for (final (k, p) in const [('ups.maker', 'maker'), ('ups.model', 'model'), ('usb.serial', 'serial'), ('ups.serial', 'serial'), ('usb.id', 'usbId')])
        if (_reading(r, k) != null) p: Prop(_reading(r, k), src: 'declared'),
    };
    out.add(LinkSuggestion(
      match: match,
      observes: t.id,
      type: r.node?.type ?? 'thing',
      name: r.name,
      parentObserves: t.host,
      props: props,
      relinkItem: had?.id,
    ));
  }
  return out;
}

/// Accepts [s]: a new item (state `owned`, under the host's item when there
/// is one), or the relinked existing item.
Item acceptLink(ModelStore store, LinkSuggestion s, {String? id, DateTime? now}) {
  if (s.relinkItem != null) {
    final it = store.get(s.relinkItem!);
    if (it != null) {
      it.observes = s.observes;
      it.extra['match'] = s.match;
      return store.put(it, now: now);
    }
  }
  String? parent;
  if (s.parentObserves != null) {
    for (final i in store.ids()) {
      final it = store.get(i);
      if (it != null && it.state != 'retired' && _observed(it).contains(s.parentObserves)) parent = it.id;
    }
  }
  final it = Item(
    id: id ?? newModelId(),
    type: s.type,
    name: s.name,
    state: 'owned',
    parent: parent,
    observes: s.observes,
    props: {...s.props},
    extra: {'match': s.match, if (s.also.isNotEmpty) 'alsoObserves': s.also},
  );
  return store.put(it, now: now);
}
