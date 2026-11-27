import 'dart:convert';
import 'dart:io';

import 'model.dart' show validModelId;

/// Connector and port library (roadmap row 23). Read-only built-in
/// knowledge in code (so it loads instantly on a phone, no asset files),
/// plus a user's additions in `model/library/<id>.json`. FORMAT.md
/// "Library".
///
/// Four separate things, never inferred from one another:
/// * **shape** ([ConnectorDef]): what physically mates (`fit`), its pins and
///   gender. A USB-C shape says nothing about Thunderbolt.
/// * **pinout** ([ConnectorDef.pins]): what each contact is, when standard.
/// * **signal** ([SignalDef]): a protocol, level or power standard, with
///   its limits and direction. A connector lists the signals it *may*
///   carry; a real port must declare which it does.
/// * **limits**: numbers from the standard (`src: spec`), never a claim
///   about a particular device.
///
/// Built from the old app's planning catalogue (T301 `ConnectorCatalog`,
/// over 100 shapes), with pinouts, limits and Eurorack added.
class ConnectorDef {
  const ConnectorDef(this.id, this.label, this.family, this.fit, {this.signals = const [], this.pins = const {}, this.genders = const ['plug', 'receptacle'], this.alsoMates = const [], this.note});
  final String id, label, family;

  /// Shapes with the same fit key mate (plug into receptacle).
  final String fit;

  /// Signals this shape may carry (ids in [Library.signals]).
  final List<String> signals;

  /// Contact -> what it is, when the standard fixes it.
  final Map<String, String> pins;

  /// `plug`/`receptacle`, or `genderless` (fiber LC, Anderson).
  final List<String> genders;

  /// Other fit keys this receptacle also accepts (an NL4 socket takes an
  /// NL2 plug).
  final List<String> alsoMates;
  final String? note;

  Map<String, Object?> toJson() => {
        'id': id, 'label': label, 'family': family, 'fit': fit,
        if (signals.isNotEmpty) 'signals': signals,
        if (pins.isNotEmpty) 'pins': pins,
        'genders': genders,
        if (alsoMates.isNotEmpty) 'alsoMates': alsoMates,
        if (note != null) 'note': note,
      };

  static ConnectorDef? fromJson(Object? j) {
    if (j is! Map || j['id'] is! String || j['label'] is! String || j['family'] is! String || j['fit'] is! String) return null;
    if (!validModelId(j['id'] as String)) return null;
    List<String> l(Object? v) => v is List ? [for (final x in v) '$x'] : const [];
    return ConnectorDef(j['id'] as String, j['label'] as String, j['family'] as String, j['fit'] as String,
        signals: l(j['signals']),
        pins: j['pins'] is Map ? {for (final e in (j['pins'] as Map).entries) '${e.key}': '${e.value}'} : const {},
        genders: j['genders'] is List ? l(j['genders']) : const ['plug', 'receptacle'],
        alsoMates: l(j['alsoMates']),
        note: j['note'] as String?);
  }
}

/// A protocol, level or power standard.
class SignalDef {
  const SignalDef(this.id, this.label, this.family, this.kind, {this.limits = const {}, this.direction = 'both', this.note});
  final String id, label, family;

  /// `data`, `video`, `audio`, `power`, `control`.
  final String kind;

  /// Numbers from the standard: `gbps`, `v`, `vMin`, `a`, `w`, `dbu`,
  /// `impedanceOhm`, `polarity` (`center-negative` ...), `m` (max length).
  final Map<String, Object> limits;

  /// `both`, or `out-to-in` (a source drives a sink: line out to line in,
  /// a supply to a load).
  final String direction;
  final String? note;

  Map<String, Object?> toJson() => {'id': id, 'label': label, 'family': family, 'kind': kind, if (limits.isNotEmpty) 'limits': limits, 'direction': direction, if (note != null) 'note': note};

  static SignalDef? fromJson(Object? j) {
    if (j is! Map || j['id'] is! String || j['label'] is! String || j['family'] is! String || j['kind'] is! String) return null;
    if (!validModelId(j['id'] as String)) return null;
    return SignalDef(j['id'] as String, j['label'] as String, j['family'] as String, j['kind'] as String,
        limits: j['limits'] is Map ? {for (final e in (j['limits'] as Map).entries) if (e.value is num || e.value is String) '${e.key}': e.value as Object} : const {},
        direction: j['direction'] == 'out-to-in' ? 'out-to-in' : 'both',
        note: j['note'] as String?);
  }
}

const libraryFamilies = ['usb', 'display', 'network', 'power', 'audio', 'eurorack', 'storage', 'rf', 'control'];

class Library {
  Library._(this.connectors, this.signals, this.problems);
  final Map<String, ConnectorDef> connectors;
  final Map<String, SignalDef> signals;

  /// User files that were not used, with why.
  final Map<String, String> problems;

  static Library? _builtIn;
  static Library get builtIn => _builtIn ??= Library._({for (final c in _connectors) c.id: c}, {for (final s in _signals) s.id: s}, const {});

  /// The built-in library plus `model/library/*.json` in [root]: each file
  /// holds `connectors` and/or `signals` lists. A user entry may not replace
  /// a built-in id.
  static Library load(String? root) {
    final b = builtIn;
    if (root == null) return b;
    final d = Directory('$root${Platform.pathSeparator}model${Platform.pathSeparator}library');
    if (!d.existsSync()) return b;
    final cs = {...b.connectors}, ss = {...b.signals};
    final problems = <String, String>{};
    for (final f in d.listSync().whereType<File>()) {
      final n = f.uri.pathSegments.last;
      if (!n.endsWith('.json') || n.startsWith('.') || n.contains('.sync-conflict-')) continue;
      try {
        if (f.lengthSync() > 256 * 1024) throw const FormatException('over 256 KB');
        final j = jsonDecode(f.readAsStringSync());
        if (j is! Map) throw const FormatException('not an object');
        for (final x in (j['signals'] as List?) ?? const []) {
          final s = SignalDef.fromJson(x);
          if (s == null) throw FormatException('a signal is missing id, label, family or kind: $x');
          if (b.signals.containsKey(s.id)) throw FormatException('signal ${s.id} is built in');
          ss[s.id] = s;
        }
        for (final x in (j['connectors'] as List?) ?? const []) {
          final c = ConnectorDef.fromJson(x);
          if (c == null) throw FormatException('a connector is missing id, label, family or fit: $x');
          if (b.connectors.containsKey(c.id)) throw FormatException('connector ${c.id} is built in');
          cs[c.id] = c;
        }
      } catch (e) {
        problems[n] = e is FormatException ? e.message : 'not valid JSON';
      }
    }
    return Library._(cs, ss, problems);
  }

  List<ConnectorDef> family(String f) => [for (final c in connectors.values) if (c.family == f) c];

  /// Whether a [plug] goes into a [receptacle] physically. Says nothing
  /// about what will work through it (row 24).
  bool mates(String plug, String receptacle) {
    final p = connectors[plug], r = connectors[receptacle];
    if (p == null || r == null) return false;
    return p.fit == r.fit || r.alsoMates.contains(p.fit);
  }

  /// Signals both shapes may carry.
  List<SignalDef> common(String a, String b) {
    final x = connectors[a], y = connectors[b];
    if (x == null || y == null) return const [];
    return [for (final s in x.signals) if (y.signals.contains(s) && signals[s] != null) signals[s]!];
  }
}

// ---------------------------------------------------------------------------
// Built-in data. Limits are from the published standards.

const _connectors = <ConnectorDef>[
  // USB: shape and protocol are separate.
  ConnectorDef('usb-a', 'USB Type-A', 'usb', 'usb-a', signals: ['usb-1', 'usb-2', 'usb-bc', 'usb-default-power'], pins: {'1': 'VBUS', '2': 'D-', '3': 'D+', '4': 'GND'}),
  ConnectorDef('usb-a-ss', 'USB Type-A SuperSpeed', 'usb', 'usb-a', signals: ['usb-1', 'usb-2', 'usb-3-5g', 'usb-3-10g', 'usb-bc', 'usb-default-power'], pins: {'1': 'VBUS', '2': 'D-', '3': 'D+', '4': 'GND', '5': 'SSRX-', '6': 'SSRX+', '7': 'GND_DRAIN', '8': 'SSTX-', '9': 'SSTX+'}, note: 'mates with plain Type-A at USB 2 speed'),
  ConnectorDef('usb-b', 'USB Type-B', 'usb', 'usb-b', signals: ['usb-1', 'usb-2', 'usb-default-power'], pins: {'1': 'VBUS', '2': 'D-', '3': 'D+', '4': 'GND'}),
  ConnectorDef('usb-b3', 'USB 3 Type-B', 'usb', 'usb-b3', signals: ['usb-1', 'usb-2', 'usb-3-5g', 'usb-default-power'], alsoMates: ['usb-b'], note: 'a USB 3 Type-B socket also takes a USB 2 Type-B plug'),
  ConnectorDef('mini-usb-b', 'Mini-USB Type-B', 'usb', 'mini-usb-b', signals: ['usb-1', 'usb-2', 'usb-default-power']),
  ConnectorDef('micro-usb-b', 'Micro-USB Type-B', 'usb', 'micro-usb-b', signals: ['usb-1', 'usb-2', 'usb-bc', 'usb-default-power']),
  ConnectorDef('micro-usb-b3', 'Micro-USB 3 Type-B', 'usb', 'micro-usb-b3', signals: ['usb-2', 'usb-3-5g', 'usb-default-power'], alsoMates: ['micro-usb-b']),
  ConnectorDef('usb-c', 'USB Type-C', 'usb', 'usb-c',
      signals: ['usb-2', 'usb-3-5g', 'usb-3-10g', 'usb-3-20g', 'usb4-40g', 'usb4-80g', 'usb-c-current', 'usb-pd-spr', 'usb-pd-epr', 'dp-alt', 'thunderbolt-3', 'thunderbolt-4', 'thunderbolt-5', 'usb-bc'],
      pins: {'A1/B1/A12/B12': 'GND', 'A4/B4/A9/B9': 'VBUS', 'A5': 'CC1', 'B5': 'CC2 (VCONN)', 'A6/B6': 'D+', 'A7/B7': 'D-', 'A2/A3,B10/B11': 'TX1/RX1', 'B2/B3,A10/A11': 'TX2/RX2', 'A8/B8': 'SBU1/SBU2'},
      note: 'the shape alone guarantees USB 2 and 5 V only; every other signal must be declared by the port and the cable'),
  ConnectorDef('lightning', 'Apple Lightning', 'usb', 'lightning', signals: ['usb-2', 'usb-default-power']),

  // Display.
  ConnectorDef('hdmi-a', 'HDMI Type A', 'display', 'hdmi-a', signals: ['hdmi-1.4', 'hdmi-2.0', 'hdmi-2.1-frl', 'hdmi-cec', 'hdmi-arc', 'hdmi-earc']),
  ConnectorDef('hdmi-c', 'Mini HDMI (Type C)', 'display', 'hdmi-c', signals: ['hdmi-1.4', 'hdmi-2.0', 'hdmi-cec']),
  ConnectorDef('hdmi-d', 'Micro HDMI (Type D)', 'display', 'hdmi-d', signals: ['hdmi-1.4', 'hdmi-2.0', 'hdmi-cec']),
  ConnectorDef('displayport', 'DisplayPort', 'display', 'displayport', signals: ['dp-1.2', 'dp-1.4', 'dp-2.1', 'dp-dual-mode'], note: 'DP++ (dual mode) ports can drive HDMI/DVI through a passive adapter; plain DP ports need an active one'),
  ConnectorDef('mini-displayport', 'Mini DisplayPort', 'display', 'mini-displayport', signals: ['dp-1.2', 'dp-dual-mode', 'thunderbolt-1-2']),
  ConnectorDef('dvi-d-sl', 'DVI-D single link', 'display', 'dvi-d', signals: ['dvi-sl'], note: 'a single-link plug fits a dual-link socket'),
  ConnectorDef('dvi-d-dl', 'DVI-D dual link', 'display', 'dvi-d', signals: ['dvi-sl', 'dvi-dl']),
  ConnectorDef('dvi-i-dl', 'DVI-I dual link', 'display', 'dvi-i', signals: ['dvi-sl', 'dvi-dl', 'vga-analog'], alsoMates: ['dvi-d', 'dvi-a'], note: 'a DVI-I socket takes DVI-D and DVI-A plugs; a DVI-D socket does not take DVI-I'),
  ConnectorDef('dvi-a', 'DVI-A', 'display', 'dvi-a', signals: ['vga-analog']),
  ConnectorDef('vga', 'VGA (DE-15)', 'display', 'de15', signals: ['vga-analog'], pins: {'1': 'Red', '2': 'Green', '3': 'Blue', '13': 'HSync', '14': 'VSync', '12': 'DDC SDA', '15': 'DDC SCL', '9': '+5 V (DDC)'}),
  ConnectorDef('sdi-bnc', 'SDI (BNC)', 'display', 'bnc', signals: ['sdi-3g', 'sdi-12g']),
  ConnectorDef('composite-rca', 'Composite video (RCA)', 'display', 'rca', signals: ['composite-video']),

  // Network.
  ConnectorDef('rj45', '8P8C (RJ45)', 'network', '8p8c', signals: ['eth-100', 'eth-1g', 'eth-2.5g', 'eth-10g', 'poe-af', 'poe-at', 'poe-bt'],
      pins: {'1/2': 'pair A (T568B: orange)', '3/6': 'pair B (green)', '4/5': 'pair C (blue)', '7/8': 'pair D (brown)'}),
  ConnectorDef('ethercon', 'Neutrik etherCON', 'network', '8p8c', signals: ['eth-100', 'eth-1g', 'poe-af', 'poe-at'], note: 'takes a normal RJ45 plug; latch for touring'),
  ConnectorDef('sfp', 'SFP cage', 'network', 'sfp', signals: ['eth-1g'], genders: ['receptacle', 'module']),
  ConnectorDef('sfp-plus', 'SFP+ cage', 'network', 'sfp', signals: ['eth-1g', 'eth-10g'], genders: ['receptacle', 'module'], note: 'most SFP+ cages also run 1G SFP modules; check the device'),
  ConnectorDef('sfp28', 'SFP28 cage', 'network', 'sfp', signals: ['eth-10g', 'eth-25g'], genders: ['receptacle', 'module']),
  ConnectorDef('qsfp28', 'QSFP28 cage', 'network', 'qsfp', signals: ['eth-40g', 'eth-100g'], genders: ['receptacle', 'module']),
  ConnectorDef('lc-duplex', 'LC duplex fiber', 'network', 'lc-duplex', signals: ['fiber-sm', 'fiber-mm'], genders: ['genderless']),
  ConnectorDef('sc', 'SC fiber', 'network', 'sc', signals: ['fiber-sm', 'fiber-mm'], genders: ['genderless']),
  ConnectorDef('mpo', 'MPO/MTP fiber', 'network', 'mpo', signals: ['fiber-sm', 'fiber-mm'], genders: ['plug', 'receptacle'], note: 'pinned and unpinned ends, and polarity method A/B/C, must match'),
  ConnectorDef('rj11', 'RJ11 (6P2C/6P4C)', 'network', '6p', signals: ['telephone']),

  // AC/DC power.
  ConnectorDef('nema-5-15', 'NEMA 5-15 (US outlet)', 'power', 'nema-5-15', signals: ['ac-120v-15a']),
  ConnectorDef('nema-5-20', 'NEMA 5-20', 'power', 'nema-5-20', signals: ['ac-120v-20a'], alsoMates: ['nema-5-15'], note: 'a 5-20 outlet also takes a 5-15 plug'),
  ConnectorDef('iec-c13', 'IEC C13 (cable end)', 'power', 'iec-c13', signals: ['ac-mains-10a'], genders: ['plug']),
  ConnectorDef('iec-c14', 'IEC C14 (device inlet)', 'power', 'iec-c13', signals: ['ac-mains-10a'], genders: ['receptacle']),
  ConnectorDef('iec-c5', 'IEC C5 cloverleaf', 'power', 'iec-c5', signals: ['ac-mains-2.5a']),
  ConnectorDef('iec-c7', 'IEC C7 figure-eight', 'power', 'iec-c7', signals: ['ac-mains-2.5a']),
  ConnectorDef('iec-c19', 'IEC C19/C20', 'power', 'iec-c19', signals: ['ac-mains-16a']),
  ConnectorDef('powercon', 'powerCON (A in / B out)', 'power', 'powercon', signals: ['ac-mains-20a'], note: 'A (blue, in) and B (grey, out) are keyed apart; not for switching under load'),
  ConnectorDef('powercon-true1', 'powerCON TRUE1', 'power', 'powercon-true1', signals: ['ac-mains-16a']),
  ConnectorDef('dc-5521', 'DC barrel 5.5/2.1 mm', 'power', 'barrel-5521', signals: ['dc-9v-cn', 'dc-12v-cp', 'dc-18v-cn', 'dc-generic'], note: 'not interchangeable with 5.5/2.5 mm: a 2.1 plug won\'t go on a 2.5 pin reliably and a 2.5 plug is loose on a 2.1 pin'),
  ConnectorDef('dc-5525', 'DC barrel 5.5/2.5 mm', 'power', 'barrel-5525', signals: ['dc-12v-cp', 'dc-19v-cp', 'dc-generic']),
  ConnectorDef('dc-35135', 'DC barrel 3.5/1.35 mm', 'power', 'barrel-35135', signals: ['dc-5v-cp', 'dc-generic']),
  ConnectorDef('xt60', 'XT60', 'power', 'xt60', signals: ['dc-generic'], note: 'rated about 30 A continuous'),
  ConnectorDef('anderson-pp45', 'Anderson Powerpole 45 A', 'power', 'anderson-pp45', signals: ['dc-generic'], genders: ['genderless']),
  ConnectorDef('atx-24', 'ATX 24-pin', 'power', 'atx-24', signals: ['atx-rails'], alsoMates: ['atx-20']),
  ConnectorDef('eps-8', 'EPS 8-pin CPU', 'power', 'eps-8', signals: ['pc-12v']),
  ConnectorDef('pcie-8', 'PCIe 8-pin (6+2)', 'power', 'pcie-8', signals: ['pc-12v'], note: 'keyed differently from EPS 8-pin: forcing one into the other shorts 12 V to ground'),
  ConnectorDef('12v-2x6', '12V-2x6 (PCIe 5)', 'power', '12v-2x6', signals: ['pc-12v-600w']),
  ConnectorDef('sata-power', 'SATA power 15-pin', 'power', 'sata-power', signals: ['sata-power-rails']),
  ConnectorDef('molex-4', 'Molex 4-pin peripheral', 'power', 'molex-4', signals: ['sata-power-rails']),

  // Audio.
  ConnectorDef('xlr3', 'XLR 3-pin', 'audio', 'xlr3', signals: ['mic-level', 'line-pro', 'aes3', 'phantom-48v', 'dmx512'], pins: {'1': 'shield/ground', '2': 'hot (+)', '3': 'cold (-)'}, note: 'male pins send, female sockets receive by convention: outputs are male, inputs female'),
  ConnectorDef('xlr4', 'XLR 4-pin', 'audio', 'xlr4', signals: ['dc-12v-cp', 'dc-generic'], note: 'common for 12 V DC on video and intercom gear; no audio standard'),
  ConnectorDef('xlr5', 'XLR 5-pin', 'audio', 'xlr5', signals: ['dmx512'], pins: {'1': 'common', '2': 'data -', '3': 'data +', '4/5': 'spare'}),
  ConnectorDef('ts-635', '1/4" TS (6.35 mm mono)', 'audio', 'jack-635', signals: ['instrument-level', 'line-consumer', 'line-pro', 'speaker-level', 'expression-pedal'], pins: {'tip': 'signal', 'sleeve': 'ground'}, note: 'a TS plug in a TRS socket shorts ring to sleeve: fine for balanced inputs, not for insert or stereo outputs'),
  ConnectorDef('trs-635', '1/4" TRS (6.35 mm)', 'audio', 'jack-635', signals: ['line-pro', 'line-consumer', 'headphone', 'insert', 'expression-pedal'], pins: {'tip': 'hot / left / send', 'ring': 'cold / right / return', 'sleeve': 'ground'}),
  ConnectorDef('ts-35', '3.5 mm TS', 'audio', 'jack-35', signals: ['line-consumer', 'cv', 'gate', 'eurorack-audio'], pins: {'tip': 'signal', 'sleeve': 'ground'}),
  ConnectorDef('trs-35', '3.5 mm TRS', 'audio', 'jack-35', signals: ['line-consumer', 'headphone', 'midi-trs-a', 'midi-trs-b'], pins: {'tip': 'left', 'ring': 'right', 'sleeve': 'ground'}, note: 'TRS MIDI type A and type B swap tip and ring: the shape mates, the signal does not'),
  ConnectorDef('trrs-35', '3.5 mm TRRS', 'audio', 'jack-35', signals: ['headset-ctia', 'headset-omtp'], note: 'CTIA (sleeve = mic) and OMTP (ring 2 = mic) are wired differently'),
  ConnectorDef('rca', 'RCA (phono)', 'audio', 'rca', signals: ['line-consumer', 'spdif-coax', 'composite-video'], pins: {'center': 'signal', 'shell': 'ground'}),
  ConnectorDef('speakon-nl2', 'speakON NL2', 'audio', 'speakon-nl2', signals: ['speaker-level'], pins: {'1+': 'speaker +', '1-': 'speaker -'}),
  ConnectorDef('speakon-nl4', 'speakON NL4', 'audio', 'speakon-nl4', signals: ['speaker-level'], pins: {'1+/1-': 'first pair', '2+/2-': 'second pair (bi-amp)'}, alsoMates: ['speakon-nl2'], note: 'an NL4 socket takes an NL2 plug (pins 1+/1- only)'),
  ConnectorDef('din5-midi', '5-pin DIN (MIDI)', 'audio', 'din5-180', signals: ['midi-din'], pins: {'4': 'current source (+5 V through 220 Ω)', '5': 'current sink (data)', '2': 'shield (out only)'}),
  ConnectorDef('toslink', 'TOSLINK optical', 'audio', 'toslink', signals: ['spdif-optical', 'adat']),
  ConnectorDef('bnc-wordclock', 'BNC (word clock)', 'audio', 'bnc', signals: ['word-clock', 'spdif-coax']),

  // Eurorack.
  ConnectorDef('eurorack-10', 'Eurorack power 10-pin (2x5)', 'eurorack', 'idc-2x5', signals: ['eurorack-12v'], pins: {'1/2': '-12 V (red stripe)', '3-8': 'GND', '9/10': '+12 V'}, note: 'reversed in the socket it puts -12 V on +12 V: check the red stripe'),
  ConnectorDef('eurorack-16', 'Eurorack power 16-pin (2x8)', 'eurorack', 'idc-2x8', signals: ['eurorack-12v', 'eurorack-5v', 'eurorack-cv-gate-bus'], pins: {'1/2': '-12 V (red stripe)', '3-8': 'GND', '9/10': '+12 V', '11/12': '+5 V', '13/14': 'CV bus', '15/16': 'gate bus'}),
  ConnectorDef('eurorack-jack', '3.5 mm patch jack (Eurorack)', 'eurorack', 'jack-35', signals: ['cv', 'gate', 'eurorack-audio', 'v-oct']),

  // Storage and expansion.
  ConnectorDef('sata-data', 'SATA data 7-pin', 'storage', 'sata-data', signals: ['sata-3']),
  ConnectorDef('m2-m', 'M.2 key M', 'storage', 'm2-m', signals: ['nvme-pcie3-x4', 'nvme-pcie4-x4', 'nvme-pcie5-x4', 'sata-3'], note: 'a key M slot may wire PCIe, SATA or both: the board must say'),
  ConnectorDef('m2-b', 'M.2 key B', 'storage', 'm2-b', signals: ['sata-3', 'pcie3-x2']),
  ConnectorDef('m2-ae', 'M.2 key A/E', 'storage', 'm2-ae', signals: ['pcie3-x1', 'usb-2'], note: 'Wi-Fi cards'),
  ConnectorDef('u2', 'U.2 (SFF-8639)', 'storage', 'sff8639', signals: ['nvme-pcie4-x4', 'sas-12g', 'sata-3']),
  ConnectorDef('pcie-x1', 'PCIe x1 slot', 'storage', 'pcie-x1', signals: ['pcie3-x1', 'pcie4-x1']),
  ConnectorDef('pcie-x4', 'PCIe x4 slot', 'storage', 'pcie-x4', signals: ['pcie3-x4', 'pcie4-x4'], alsoMates: ['pcie-x1']),
  ConnectorDef('pcie-x16', 'PCIe x16 slot', 'storage', 'pcie-x16', signals: ['pcie3-x16', 'pcie4-x16', 'pcie5-x16'], alsoMates: ['pcie-x1', 'pcie-x4', 'pcie-x8'], note: 'a shorter card fits a longer slot; the slot may be wired with fewer lanes than its length'),
  ConnectorDef('sff-8643', 'Mini-SAS HD internal (SFF-8643)', 'storage', 'sff8643', signals: ['sas-12g', 'sata-3']),

  // RF.
  ConnectorDef('sma', 'SMA', 'rf', 'sma', signals: ['rf-50'], note: 'RP-SMA looks alike but has the pin swapped: it does not mate'),
  ConnectorDef('rp-sma', 'RP-SMA', 'rf', 'rp-sma', signals: ['rf-50']),
  ConnectorDef('f-type', 'F-type', 'rf', 'f-type', signals: ['rf-75']),
  ConnectorDef('n-type', 'N-type', 'rf', 'n-type', signals: ['rf-50']),
  ConnectorDef('u-fl', 'U.FL / IPEX MHF1', 'rf', 'u-fl', signals: ['rf-50']),

  // Control.
  ConnectorDef('rs232-de9', 'RS-232 (DE-9)', 'control', 'de9', signals: ['rs232'], pins: {'2': 'RX (DTE)', '3': 'TX (DTE)', '5': 'GND'}),
  ConnectorDef('gpio-254', '2.54 mm header', 'control', 'header-254', signals: ['gpio-3v3', 'i2c', 'uart-ttl', 'spi']),
  ConnectorDef('qwiic', 'JST SH 4-pin (Qwiic)', 'control', 'jst-sh4', signals: ['i2c'], pins: {'1': 'GND', '2': '3.3 V', '3': 'SDA', '4': 'SCL'}),
  ConnectorDef('fan-4', '4-pin PWM fan', 'control', 'fan-4', signals: ['fan-12v'], pins: {'1': 'GND', '2': '+12 V', '3': 'tach', '4': 'PWM'}, alsoMates: ['fan-3'], note: 'a 3-pin fan fits a 4-pin header and runs at full or voltage-controlled speed'),
];

const _signals = <SignalDef>[
  // USB data and power.
  SignalDef('usb-1', 'USB 1.1 (12 Mbit/s)', 'usb', 'data', limits: {'gbps': 0.012, 'm': 3}),
  SignalDef('usb-2', 'USB 2.0 (480 Mbit/s)', 'usb', 'data', limits: {'gbps': 0.48, 'm': 5}),
  SignalDef('usb-3-5g', 'USB 5Gbps (3.2 Gen 1)', 'usb', 'data', limits: {'gbps': 5, 'm': 3}),
  SignalDef('usb-3-10g', 'USB 10Gbps (3.2 Gen 2)', 'usb', 'data', limits: {'gbps': 10, 'm': 1}),
  SignalDef('usb-3-20g', 'USB 20Gbps (3.2 Gen 2x2)', 'usb', 'data', limits: {'gbps': 20, 'm': 1}, note: 'USB-C only'),
  SignalDef('usb4-40g', 'USB4 40Gbps', 'usb', 'data', limits: {'gbps': 40, 'm': 0.8}),
  SignalDef('usb4-80g', 'USB4 v2 80Gbps', 'usb', 'data', limits: {'gbps': 80, 'm': 1}),
  SignalDef('usb-default-power', 'USB default power', 'usb', 'power', direction: 'out-to-in', limits: {'v': 5, 'a': 0.5}, note: '0.9 A on USB 3 ports'),
  SignalDef('usb-bc', 'USB Battery Charging 1.2', 'usb', 'power', direction: 'out-to-in', limits: {'v': 5, 'a': 1.5}),
  SignalDef('usb-c-current', 'USB Type-C current', 'usb', 'power', direction: 'out-to-in', limits: {'v': 5, 'a': 3}, note: 'advertised on CC: default, 1.5 A or 3 A'),
  SignalDef('usb-pd-spr', 'USB Power Delivery (SPR)', 'usb', 'power', direction: 'out-to-in', limits: {'v': 20, 'a': 5, 'w': 100}, note: 'over 3 A needs a 5 A e-marked cable'),
  SignalDef('usb-pd-epr', 'USB Power Delivery (EPR)', 'usb', 'power', direction: 'out-to-in', limits: {'v': 48, 'a': 5, 'w': 240}, note: 'needs an EPR (240 W) e-marked cable'),
  SignalDef('dp-alt', 'DisplayPort Alt Mode over USB-C', 'usb', 'video', direction: 'out-to-in', limits: {'gbps': 32.4}, note: 'only when the port and the cable declare it'),
  SignalDef('thunderbolt-1-2', 'Thunderbolt 1/2', 'usb', 'data', limits: {'gbps': 20}),
  SignalDef('thunderbolt-3', 'Thunderbolt 3', 'usb', 'data', limits: {'gbps': 40, 'w': 100}),
  SignalDef('thunderbolt-4', 'Thunderbolt 4', 'usb', 'data', limits: {'gbps': 40, 'w': 100}),
  SignalDef('thunderbolt-5', 'Thunderbolt 5', 'usb', 'data', limits: {'gbps': 80, 'w': 240}),

  // Display.
  SignalDef('hdmi-1.4', 'HDMI 1.4 (TMDS 10.2 Gbit/s)', 'display', 'video', direction: 'out-to-in', limits: {'gbps': 10.2}),
  SignalDef('hdmi-2.0', 'HDMI 2.0 (TMDS 18 Gbit/s)', 'display', 'video', direction: 'out-to-in', limits: {'gbps': 18}),
  SignalDef('hdmi-2.1-frl', 'HDMI 2.1 FRL (48 Gbit/s)', 'display', 'video', direction: 'out-to-in', limits: {'gbps': 48}, note: 'needs an Ultra High Speed cable'),
  SignalDef('hdmi-cec', 'HDMI CEC', 'display', 'control'),
  SignalDef('hdmi-arc', 'HDMI ARC', 'display', 'audio', direction: 'out-to-in'),
  SignalDef('hdmi-earc', 'HDMI eARC', 'display', 'audio', direction: 'out-to-in'),
  SignalDef('dp-1.2', 'DisplayPort 1.2 (HBR2)', 'display', 'video', direction: 'out-to-in', limits: {'gbps': 21.6}),
  SignalDef('dp-1.4', 'DisplayPort 1.4 (HBR3)', 'display', 'video', direction: 'out-to-in', limits: {'gbps': 32.4}),
  SignalDef('dp-2.1', 'DisplayPort 2.1 (UHBR20)', 'display', 'video', direction: 'out-to-in', limits: {'gbps': 80}),
  SignalDef('dp-dual-mode', 'DP++ dual mode (TMDS for HDMI/DVI)', 'display', 'video', direction: 'out-to-in', limits: {'gbps': 10.2}),
  SignalDef('dvi-sl', 'DVI single link', 'display', 'video', direction: 'out-to-in', limits: {'gbps': 4.95}),
  SignalDef('dvi-dl', 'DVI dual link', 'display', 'video', direction: 'out-to-in', limits: {'gbps': 9.9}),
  SignalDef('vga-analog', 'Analog RGB (VGA)', 'display', 'video', direction: 'out-to-in', limits: {'impedanceOhm': 75, 'v': 0.7}),
  SignalDef('sdi-3g', '3G-SDI', 'display', 'video', direction: 'out-to-in', limits: {'gbps': 2.97, 'impedanceOhm': 75}),
  SignalDef('sdi-12g', '12G-SDI', 'display', 'video', direction: 'out-to-in', limits: {'gbps': 11.88, 'impedanceOhm': 75}),
  SignalDef('composite-video', 'Composite video', 'display', 'video', direction: 'out-to-in', limits: {'impedanceOhm': 75, 'v': 1}),

  // Network.
  SignalDef('eth-100', '100BASE-TX', 'network', 'data', limits: {'gbps': 0.1, 'm': 100}),
  SignalDef('eth-1g', '1 Gigabit Ethernet', 'network', 'data', limits: {'gbps': 1, 'm': 100}),
  SignalDef('eth-2.5g', '2.5GBASE-T', 'network', 'data', limits: {'gbps': 2.5, 'm': 100}),
  SignalDef('eth-10g', '10 Gigabit Ethernet', 'network', 'data', limits: {'gbps': 10, 'm': 100}, note: '10GBASE-T needs Cat6a for 100 m (Cat6 about 55 m)'),
  SignalDef('eth-25g', '25 Gigabit Ethernet', 'network', 'data', limits: {'gbps': 25}),
  SignalDef('eth-40g', '40 Gigabit Ethernet', 'network', 'data', limits: {'gbps': 40}),
  SignalDef('eth-100g', '100 Gigabit Ethernet', 'network', 'data', limits: {'gbps': 100}),
  SignalDef('poe-af', 'PoE 802.3af', 'network', 'power', direction: 'out-to-in', limits: {'v': 57, 'vMin': 44, 'w': 15.4}, note: '12.95 W at the device'),
  SignalDef('poe-at', 'PoE+ 802.3at', 'network', 'power', direction: 'out-to-in', limits: {'v': 57, 'vMin': 50, 'w': 30}, note: '25.5 W at the device'),
  SignalDef('poe-bt', 'PoE++ 802.3bt', 'network', 'power', direction: 'out-to-in', limits: {'v': 57, 'vMin': 52, 'w': 90}, note: '71.3 W at the device (type 4)'),
  SignalDef('fiber-sm', 'Single-mode fiber', 'network', 'data', note: 'single-mode and multimode optics do not interoperate'),
  SignalDef('fiber-mm', 'Multimode fiber', 'network', 'data'),
  SignalDef('telephone', 'Analog telephone line', 'network', 'audio'),

  // Power.
  SignalDef('ac-120v-15a', '120 V AC, 15 A', 'power', 'power', direction: 'out-to-in', limits: {'v': 120, 'a': 15}),
  SignalDef('ac-120v-20a', '120 V AC, 20 A', 'power', 'power', direction: 'out-to-in', limits: {'v': 120, 'a': 20}),
  SignalDef('ac-mains-2.5a', 'Mains AC, 2.5 A', 'power', 'power', direction: 'out-to-in', limits: {'v': 250, 'a': 2.5}),
  SignalDef('ac-mains-10a', 'Mains AC, 10 A', 'power', 'power', direction: 'out-to-in', limits: {'v': 250, 'a': 10}, note: '15 A in North America'),
  SignalDef('ac-mains-16a', 'Mains AC, 16 A', 'power', 'power', direction: 'out-to-in', limits: {'v': 250, 'a': 16}),
  SignalDef('ac-mains-20a', 'Mains AC, 20 A', 'power', 'power', direction: 'out-to-in', limits: {'v': 250, 'a': 20}),
  SignalDef('dc-5v-cp', '5 V DC, center positive', 'power', 'power', direction: 'out-to-in', limits: {'v': 5, 'polarity': 'center-positive'}),
  SignalDef('dc-9v-cn', '9 V DC, center negative (pedal standard)', 'power', 'power', direction: 'out-to-in', limits: {'v': 9, 'polarity': 'center-negative'}, note: 'most guitar pedals; check current per pedal'),
  SignalDef('dc-12v-cp', '12 V DC, center positive', 'power', 'power', direction: 'out-to-in', limits: {'v': 12, 'polarity': 'center-positive'}),
  SignalDef('dc-18v-cn', '18 V DC, center negative', 'power', 'power', direction: 'out-to-in', limits: {'v': 18, 'polarity': 'center-negative'}),
  SignalDef('dc-19v-cp', '19 V DC, center positive (laptop)', 'power', 'power', direction: 'out-to-in', limits: {'v': 19, 'polarity': 'center-positive'}),
  SignalDef('dc-generic', 'DC, voltage and polarity as declared', 'power', 'power', direction: 'out-to-in', note: 'the port must declare voltage, polarity and current'),
  SignalDef('atx-rails', 'ATX rails (+3.3, +5, +12, -12 V, 5VSB)', 'power', 'power', direction: 'out-to-in'),
  SignalDef('pc-12v', '+12 V (CPU/GPU)', 'power', 'power', direction: 'out-to-in', limits: {'v': 12, 'w': 150}, note: 'per 8-pin connector'),
  SignalDef('pc-12v-600w', '+12 V up to 600 W', 'power', 'power', direction: 'out-to-in', limits: {'v': 12, 'w': 600}),
  SignalDef('sata-power-rails', '+3.3/+5/+12 V drive power', 'power', 'power', direction: 'out-to-in', limits: {'a': 4.5}, note: 'about 1.5 A per pin, 3 pins per rail'),

  // Audio levels (nominal).
  SignalDef('mic-level', 'Microphone level', 'audio', 'audio', direction: 'out-to-in', limits: {'dbu': -50}, note: 'roughly -60 to -40 dBu; needs a preamp'),
  SignalDef('instrument-level', 'Instrument (Hi-Z)', 'audio', 'audio', direction: 'out-to-in', limits: {'impedanceOhm': 1000000}, note: 'passive pickups want about 1 MΩ input'),
  SignalDef('line-consumer', 'Consumer line (-10 dBV)', 'audio', 'audio', direction: 'out-to-in', limits: {'dbu': -7.8}),
  SignalDef('line-pro', 'Pro line (+4 dBu), usually balanced', 'audio', 'audio', direction: 'out-to-in', limits: {'dbu': 4}),
  SignalDef('headphone', 'Headphone output', 'audio', 'audio', direction: 'out-to-in'),
  SignalDef('insert', 'Insert (send on tip, return on ring)', 'audio', 'audio'),
  SignalDef('speaker-level', 'Speaker level (amplifier output)', 'audio', 'audio', direction: 'out-to-in', note: 'never into a line or instrument input'),
  SignalDef('phantom-48v', 'Phantom power +48 V', 'audio', 'power', direction: 'out-to-in', limits: {'v': 48, 'a': 0.01}, note: 'on pins 2 and 3 through 6.8 kΩ; can damage unbalanced or ribbon gear'),
  SignalDef('aes3', 'AES3 digital audio', 'audio', 'audio', direction: 'out-to-in', limits: {'impedanceOhm': 110}),
  SignalDef('spdif-coax', 'S/PDIF coaxial', 'audio', 'audio', direction: 'out-to-in', limits: {'impedanceOhm': 75}),
  SignalDef('spdif-optical', 'S/PDIF optical', 'audio', 'audio', direction: 'out-to-in'),
  SignalDef('adat', 'ADAT Lightpipe (8 ch at 48 kHz)', 'audio', 'audio', direction: 'out-to-in'),
  SignalDef('word-clock', 'Word clock', 'audio', 'control', direction: 'out-to-in', limits: {'impedanceOhm': 75}),
  SignalDef('midi-din', 'MIDI 1.0 (5-pin DIN)', 'audio', 'control', direction: 'out-to-in', limits: {'m': 15}),
  SignalDef('midi-trs-a', 'MIDI over TRS, type A', 'audio', 'control', direction: 'out-to-in', note: 'tip = sink, ring = source; type B is reversed'),
  SignalDef('midi-trs-b', 'MIDI over TRS, type B', 'audio', 'control', direction: 'out-to-in', note: 'tip = source, ring = sink'),
  SignalDef('expression-pedal', 'Expression pedal (TRS pot)', 'audio', 'control', note: 'tip/ring roles differ between makers'),
  SignalDef('headset-ctia', 'Headset TRRS (CTIA)', 'audio', 'audio'),
  SignalDef('headset-omtp', 'Headset TRRS (OMTP)', 'audio', 'audio'),
  SignalDef('dmx512', 'DMX512', 'control', 'control', direction: 'out-to-in', limits: {'impedanceOhm': 120}, note: 'needs 120 Ω cable and a terminator; not microphone cable'),

  // Eurorack.
  SignalDef('eurorack-12v', 'Eurorack ±12 V', 'eurorack', 'power', direction: 'out-to-in', limits: {'v': 12}),
  SignalDef('eurorack-5v', 'Eurorack +5 V', 'eurorack', 'power', direction: 'out-to-in', limits: {'v': 5}),
  SignalDef('eurorack-cv-gate-bus', 'Eurorack CV/gate bus', 'eurorack', 'control'),
  SignalDef('cv', 'Control voltage (±5 V typical, ±10 V max)', 'eurorack', 'control', direction: 'out-to-in', limits: {'v': 10, 'vMin': -10}),
  SignalDef('v-oct', '1 V/octave pitch CV', 'eurorack', 'control', direction: 'out-to-in', limits: {'v': 10, 'vMin': -5}),
  SignalDef('gate', 'Gate/trigger (0 to 5-10 V)', 'eurorack', 'control', direction: 'out-to-in', limits: {'v': 10, 'vMin': 0}),
  SignalDef('eurorack-audio', 'Eurorack audio (10 V peak to peak)', 'eurorack', 'audio', direction: 'out-to-in', limits: {'v': 5, 'vMin': -5}, note: 'about 12 dB hotter than pro line level'),

  // Storage.
  SignalDef('sata-3', 'SATA III (6 Gbit/s)', 'storage', 'data', limits: {'gbps': 6, 'm': 1}),
  SignalDef('sas-12g', 'SAS 12 Gbit/s', 'storage', 'data', limits: {'gbps': 12}),
  SignalDef('pcie3-x1', 'PCIe 3.0 x1', 'storage', 'data', limits: {'gbps': 7.9}),
  SignalDef('pcie4-x1', 'PCIe 4.0 x1', 'storage', 'data', limits: {'gbps': 15.8}),
  SignalDef('pcie3-x2', 'PCIe 3.0 x2', 'storage', 'data', limits: {'gbps': 15.8}),
  SignalDef('pcie3-x4', 'PCIe 3.0 x4', 'storage', 'data', limits: {'gbps': 31.5}),
  SignalDef('pcie4-x4', 'PCIe 4.0 x4', 'storage', 'data', limits: {'gbps': 63}),
  SignalDef('pcie3-x16', 'PCIe 3.0 x16', 'storage', 'data', limits: {'gbps': 126}),
  SignalDef('pcie4-x16', 'PCIe 4.0 x16', 'storage', 'data', limits: {'gbps': 252}),
  SignalDef('pcie5-x16', 'PCIe 5.0 x16', 'storage', 'data', limits: {'gbps': 504}),
  SignalDef('nvme-pcie3-x4', 'NVMe on PCIe 3.0 x4', 'storage', 'data', limits: {'gbps': 31.5}),
  SignalDef('nvme-pcie4-x4', 'NVMe on PCIe 4.0 x4', 'storage', 'data', limits: {'gbps': 63}),
  SignalDef('nvme-pcie5-x4', 'NVMe on PCIe 5.0 x4', 'storage', 'data', limits: {'gbps': 126}),

  // RF and control.
  SignalDef('rf-50', 'RF, 50 Ω', 'rf', 'data', limits: {'impedanceOhm': 50}),
  SignalDef('rf-75', 'RF, 75 Ω (TV, satellite)', 'rf', 'data', limits: {'impedanceOhm': 75}),
  SignalDef('rs232', 'RS-232 serial', 'control', 'control', limits: {'v': 15, 'vMin': -15, 'm': 15}),
  SignalDef('gpio-3v3', 'GPIO 3.3 V logic', 'control', 'control', limits: {'v': 3.3}, note: 'most 3.3 V boards are damaged by 5 V on a pin'),
  SignalDef('i2c', 'I²C', 'control', 'control'),
  SignalDef('uart-ttl', 'UART (TTL)', 'control', 'control'),
  SignalDef('spi', 'SPI', 'control', 'control'),
  SignalDef('fan-12v', '12 V fan with tach/PWM', 'control', 'power', direction: 'out-to-in', limits: {'v': 12, 'a': 1}),
];
