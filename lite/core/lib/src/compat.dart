import 'library.dart';

/// Compatibility engine (roadmap row 24): checks a whole chain, port to
/// cable or adapter to port, layer by layer, and says why. Matching plugs
/// never mean compatible: the shape only says what mates; signals,
/// power and levels are checked from what each part declares, falling
/// back to what its shape *may* carry, marked as estimated.
///
/// Layers:
/// * `physical`: each joint mates (shape and gender);
/// * `protocol`: a data, video or control signal gets through every part
///   (and the one asked for, if any);
/// * `electrical`: power on the path has the voltage, polarity and current
///   the receiving end accepts;
/// * `level`: audio leaving one end is a level the other end takes.
///
/// Each layer's verdict is `ok`, `fail` or `unknown`, with evidence
/// `verified` (from declarations on both sides), `estimated` (from the
/// library's possibilities) or `unknown`, and the reasons in words.
class Port {
  const Port(this.name, this.connector, {this.gender, this.dir = 'both', this.signals, this.power});
  final String name;

  /// A connector id in the [Library].
  final String connector;

  /// `plug` or `receptacle`; null = the library's usual for a device port
  /// (receptacle).
  final String? gender;

  /// `out`, `in` or `both`.
  final String dir;

  /// The signals this port really supports; null = not declared (only the
  /// shape's possibilities are known).
  final List<String>? signals;

  /// What it supplies (dir out) or accepts (dir in): `v`, `vMin`, `a`,
  /// `polarity`.
  final Map<String, Object>? power;
}

/// A cable or adapter between two connectors.
class Link {
  const Link(this.name, this.a, this.b, {this.passes, this.converts = const {}, this.maxA, this.maxGbps, this.aGender = 'plug', this.bGender = 'plug'});
  final String name;

  /// Connector ids at each end.
  final String a, b;
  final String aGender, bGender;

  /// Signals it carries unchanged; null = whatever both ends' shapes may.
  final List<String>? passes;

  /// Active conversion: input signal -> output signal.
  final Map<String, String> converts;
  final double? maxA, maxGbps;
}

class LayerVerdict {
  LayerVerdict(this.layer, this.verdict, this.evidence, this.reasons);
  final String layer, verdict, evidence;
  final List<String> reasons;
  @override
  String toString() => '$layer: $verdict ($evidence) ${reasons.join('; ')}';
}

class ChainVerdict {
  ChainVerdict(this.layers);
  final List<LayerVerdict> layers;
  LayerVerdict operator [](String l) => layers.firstWhere((x) => x.layer == l);

  /// `fail` if any layer fails, else `unknown` if any is unknown, else `ok`.
  String get verdict => layers.any((l) => l.verdict == 'fail') ? 'fail' : (layers.any((l) => l.verdict == 'unknown') ? 'unknown' : 'ok');
}

ChainVerdict checkChain(Port from, List<Link> links, Port to, {String? want, Library? library}) {
  final lib = library ?? Library.builtIn;
  final layers = <LayerVerdict>[];
  String label(String id) => lib.connectors[id]?.label ?? id;

  // Physical: every joint, in order.
  var prevName = from.name, prevConn = from.connector, prevGender = from.gender ?? 'receptacle';
  final phys = <String>[];
  var physFail = false, physUnknown = false;
  void joint(String n1, String c1, String g1, String n2, String c2, String g2) {
    if (lib.connectors[c1] == null || lib.connectors[c2] == null) {
      physUnknown = true;
      phys.add('${lib.connectors[c1] == null ? c1 : c2} is not in the library');
      return;
    }
    final genderless = lib.connectors[c1]!.genders.contains('genderless') && lib.connectors[c2]!.genders.contains('genderless');
    if (!genderless && g1 == g2) {
      physFail = true;
      phys.add('$n1 and $n2 are both ${g1}s');
      return;
    }
    final (plug, rec) = g1 == 'plug' ? (c1, c2) : (c2, c1);
    if (!lib.mates(plug, rec)) {
      physFail = true;
      phys.add('a ${label(plug)} plug does not go into a ${label(rec)}');
    }
  }

  for (final l in links) {
    joint(prevName, prevConn, prevGender, l.name, l.a, l.aGender);
    prevName = l.name;
    prevConn = l.b;
    prevGender = l.bGender;
  }
  joint(prevName, prevConn, prevGender, to.name, to.connector, to.gender ?? 'receptacle');
  layers.add(LayerVerdict('physical', physFail ? 'fail' : (physUnknown ? 'unknown' : 'ok'), physUnknown ? 'unknown' : 'verified', phys.isEmpty ? ['every joint mates'] : phys));

  // What each end offers; estimated when not declared.
  // Evidence comes from the two ends. A cable that declares nothing is taken
  // to carry what its shapes may, and the protocol layer says so.
  final estimated = from.signals == null || to.signals == null;
  final vagueCables = [for (final l in links) if (l.passes == null && l.converts.isEmpty) l.name];
  List<String> offers(Port p) => p.signals ?? lib.connectors[p.connector]?.signals ?? const [];
  var flow = offers(from).toSet();
  for (final l in links) {
    final carries = l.passes?.toSet() ?? {...?lib.connectors[l.a]?.signals, ...?lib.connectors[l.b]?.signals};
    flow = {
      for (final s in flow)
        if (l.converts.containsKey(s)) l.converts[s]! else if (carries.contains(s) && (l.converts.isEmpty || l.passes != null)) s,
    };
  }
  final reach = flow.intersection(offers(to).toSet());
  final ev = estimated ? 'estimated' : 'verified';
  String kindOf(String s) => lib.signals[s]?.kind ?? 'data';
  String names(Iterable<String> ids) => ids.map((s) => lib.signals[s]?.label ?? s).join(', ');

  // Direction: two outputs or two inputs facing each other.
  final dirProblem = from.dir == to.dir && from.dir != 'both' ? '${from.name} and ${to.name} are both ${from.dir == 'out' ? 'outputs' : 'inputs'}' : null;

  // Protocol.
  final fromData = offers(from).where((s) => const {'data', 'video', 'control'}.contains(kindOf(s))).toSet();
  final toData = offers(to).where((s) => const {'data', 'video', 'control'}.contains(kindOf(s))).toSet();
  final dataReach = reach.where((s) => const {'data', 'video', 'control'}.contains(kindOf(s))).toList();
  if (fromData.isEmpty && toData.isEmpty && want == null) {
    layers.add(LayerVerdict('protocol', 'ok', ev, ['no data on this path']));
  } else if (want != null && !reach.contains(want)) {
    final String who;
    if (!offers(from).contains(want)) {
      who = '${from.name} ${from.signals == null ? 'is not known to support it' : 'does not support it'}';
    } else if (!offers(to).contains(want)) {
      who = '${to.name} ${to.signals == null ? 'is not known to support it' : 'does not support it'}';
    } else {
      who = '${links.where((l) => !(l.passes?.contains(want) ?? true) && !l.converts.containsKey(want)).map((l) => l.name).firstOrNull ?? 'a part of the chain'} does not carry it';
    }
    layers.add(LayerVerdict('protocol', from.signals == null || to.signals == null ? 'unknown' : 'fail', ev, ['${lib.signals[want]?.label ?? want} does not get through: $who']));
  } else if (fromData.isNotEmpty && toData.isNotEmpty && dataReach.isEmpty) {
    layers.add(LayerVerdict('protocol', 'fail', ev, ['nothing in common: ${from.name} offers ${names(fromData)}; ${to.name} takes ${names(toData)}']));
  } else if (dirProblem != null && dataReach.any((s) => lib.signals[s]?.direction == 'out-to-in')) {
    layers.add(LayerVerdict('protocol', 'fail', ev, [dirProblem]));
  } else {
    final reasons = <String>[
      if (dataReach.isNotEmpty) 'carries ${names(dataReach)}',
      if (dataReach.isNotEmpty && vagueCables.isNotEmpty) 'assuming ${vagueCables.join(', ')} carries it (not declared)',
    ];
    final cap = [for (final l in links) if (l.maxGbps != null) l.maxGbps!];
    final best = dataReach.map((s) => lib.signals[s]?.limits['gbps']).whereType<num>().fold<double>(0, (a, b) => b > a ? b.toDouble() : a);
    if (cap.isNotEmpty && best > cap.reduce((a, b) => a < b ? a : b)) reasons.add('limited to ${cap.reduce((a, b) => a < b ? a : b)} Gbit/s by the cable');
    layers.add(LayerVerdict('protocol', estimated ? (dataReach.isEmpty ? 'unknown' : 'ok') : 'ok', ev, reasons.isEmpty ? ['no data on this path'] : reasons));
  }

  // Electrical: power from a source to a sink.
  final src = from.dir == 'in' ? to : from, sink = from.dir == 'in' ? from : to;
  final powerSignals = reach.where((s) => kindOf(s) == 'power').toList();
  if (src.power == null && sink.power == null) {
    layers.add(LayerVerdict('electrical', powerSignals.isEmpty ? 'ok' : 'unknown', powerSignals.isEmpty ? ev : 'unknown',
        [powerSignals.isEmpty ? 'no power on this path' : 'power may flow (${names(powerSignals)}) but neither end declares voltage or current']));
  } else if (src.power == null || sink.power == null) {
    layers.add(LayerVerdict('electrical', 'unknown', 'unknown', ['${src.power == null ? src.name : sink.name} does not declare its power']));
  } else {
    final s = src.power!, k = sink.power!;
    final p = <String>[];
    final v = (s['v'] as num?)?.toDouble(), kv = (k['v'] as num?)?.toDouble(), kMin = (k['vMin'] as num?)?.toDouble() ?? kv;
    if (v != null && kv != null && (v > kv || (kMin != null && v < kMin))) p.add('${src.name} gives $v V; ${sink.name} takes ${kMin != kv ? '$kMin-$kv' : kv} V');
    if (s['polarity'] != null && k['polarity'] != null && s['polarity'] != k['polarity']) p.add('${src.name} is ${s['polarity']}; ${sink.name} needs ${k['polarity']}');
    final a = (s['a'] as num?)?.toDouble(), ka = (k['a'] as num?)?.toDouble();
    if (a != null && ka != null && ka > a) p.add('${sink.name} draws $ka A; ${src.name} supplies $a A');
    final cableA = [for (final l in links) if (l.maxA != null) l.maxA!];
    if (ka != null && cableA.isNotEmpty && ka > cableA.reduce((x, y) => x < y ? x : y)) p.add('the cable is rated ${cableA.reduce((x, y) => x < y ? x : y)} A; ${sink.name} draws $ka A');
    layers.add(LayerVerdict('electrical', p.isEmpty ? 'ok' : 'fail', 'verified', p.isEmpty ? ['${v ?? '?'} V${s['polarity'] != null ? ', ${s['polarity']}' : ''} within what ${sink.name} takes'] : p));
  }

  // Signal level: audio.
  final fromAudio = offers(from).where((s) => kindOf(s) == 'audio').toSet(), toAudio = offers(to).where((s) => kindOf(s) == 'audio').toSet();
  final audioReach = reach.where((s) => kindOf(s) == 'audio').toList();
  if (fromAudio.isEmpty || toAudio.isEmpty) {
    layers.add(LayerVerdict('level', 'ok', ev, ['no audio on this path']));
  } else if (audioReach.isEmpty) {
    layers.add(LayerVerdict('level', estimated ? 'unknown' : 'fail', ev, ['${from.name} sends ${names(fromAudio)}; ${to.name} takes ${names(toAudio)}']));
  } else if (dirProblem != null) {
    layers.add(LayerVerdict('level', 'fail', ev, [dirProblem]));
  } else {
    layers.add(LayerVerdict('level', 'ok', ev, ['${names(audioReach)} on both ends']));
  }
  return ChainVerdict(layers);
}
