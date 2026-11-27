import 'dart:convert';
import 'dart:io';

import 'package:monitor_core/monitor_core.dart';
import 'package:test/test.dart';

void main() {
  final lib = Library.builtIn;

  test('consistent: every signal a connector names exists, every family is filled', () {
    for (final c in lib.connectors.values) {
      for (final s in c.signals) {
        expect(lib.signals, contains(s), reason: '${c.id} -> $s');
      }
      expect(libraryFamilies, contains(c.family));
      expect(validModelId(c.id), isTrue, reason: c.id);
    }
    for (final f in libraryFamilies) {
      expect(lib.family(f), isNotEmpty, reason: f);
    }
    expect(lib.connectors.length, greaterThan(80));
  });

  test('loads fast (budget 100 ms on the tablet)', () {
    final sw = Stopwatch()..start();
    final l = Library.load(null);
    expect(l.connectors, isNotEmpty);
    expect(sw.elapsedMilliseconds, lessThan(100));
  });

  test('usb: the shape promises USB 2 and 5 V only; Thunderbolt and DP are possible, not given', () {
    expect(lib.mates('usb-c', 'usb-c'), isTrue);
    expect(lib.mates('usb-b', 'usb-b3'), isTrue);
    expect(lib.mates('usb-b3', 'usb-b'), isFalse);
    expect(lib.connectors['usb-c']!.signals, containsAll(['dp-alt', 'thunderbolt-4', 'usb-pd-epr']));
    expect(lib.signals['usb-pd-epr']!.limits['w'], 240);
    expect(lib.connectors['usb-c']!.note, contains('must be declared'));
  });

  test('display: DVI-I takes DVI-D, not the other way; DP++ is separate from DP', () {
    expect(lib.mates('dvi-d-dl', 'dvi-i-dl'), isTrue);
    expect(lib.mates('dvi-i-dl', 'dvi-d-dl'), isFalse);
    expect(lib.mates('hdmi-a', 'hdmi-c'), isFalse);
    expect(lib.signals['hdmi-2.1-frl']!.limits['gbps'], 48);
    expect(lib.connectors['displayport']!.signals, contains('dp-dual-mode'));
  });

  test('network: RJ45 into etherCON; single-mode and multimode are different signals', () {
    expect(lib.mates('rj45', 'ethercon'), isTrue);
    expect(lib.signals['poe-bt']!.limits['w'], 90);
    expect(lib.common('lc-duplex', 'sc').map((s) => s.id), containsAll(['fiber-sm', 'fiber-mm']));
    expect(lib.mates('lc-duplex', 'sc'), isFalse);
  });

  test('power: 5.5/2.1 and 5.5/2.5 barrels differ; pedal 9 V is center negative; EPS is not PCIe', () {
    expect(lib.mates('dc-5521', 'dc-5525'), isFalse);
    expect(lib.signals['dc-9v-cn']!.limits['polarity'], 'center-negative');
    expect(lib.mates('pcie-8', 'eps-8'), isFalse);
    expect(lib.mates('nema-5-15', 'nema-5-20'), isTrue);
    expect(lib.mates('iec-c13', 'iec-c14'), isTrue);
  });

  test('audio: NL2 into NL4; TRS MIDI A and B share a shape, not a signal; levels are separate', () {
    expect(lib.mates('speakon-nl2', 'speakon-nl4'), isTrue);
    expect(lib.mates('speakon-nl4', 'speakon-nl2'), isFalse);
    expect(lib.signals['midi-trs-a']!.id, isNot(lib.signals['midi-trs-b']!.id));
    expect(lib.mates('ts-635', 'trs-635'), isTrue);
    expect(lib.signals['line-pro']!.limits['dbu'], 4);
    expect(lib.connectors['xlr3']!.pins['2'], 'hot (+)');
  });

  test('eurorack: 10-pin and 16-pin headers differ; patch jacks mate with 3.5 mm TS', () {
    expect(lib.mates('eurorack-10', 'eurorack-16'), isFalse);
    expect(lib.connectors['eurorack-16']!.pins['11/12'], '+5 V');
    expect(lib.mates('ts-35', 'eurorack-jack'), isTrue);
    expect(lib.signals['eurorack-audio']!.note, contains('hotter'));
  });

  test('storage: an x4 card fits an x16 slot; M.2 B is not M', () {
    expect(lib.mates('pcie-x4', 'pcie-x16'), isTrue);
    expect(lib.mates('pcie-x16', 'pcie-x4'), isFalse);
    expect(lib.mates('m2-b', 'm2-m'), isFalse);
  });

  test('rf and control: SMA is not RP-SMA; a 3-pin fan fits a 4-pin header', () {
    expect(lib.mates('sma', 'rp-sma'), isFalse);
    expect(lib.connectors['fan-4']!.alsoMates, contains('fan-3'));
  });

  test('user additions load from model/library; built-in ids are protected', () {
    final dir = Directory.systemTemp.createTempSync('lib');
    addTearDown(() => dir.deleteSync(recursive: true));
    final d = Directory('${dir.path}/model/library')..createSync(recursive: true);
    File('${d.path}/motu.json').writeAsStringSync(jsonEncode({
      'signals': [{'id': 'motu-avb', 'label': 'MOTU AVB', 'family': 'network', 'kind': 'audio'}],
      'connectors': [{'id': 'motu-combo', 'label': 'Combo XLR/TRS', 'family': 'audio', 'fit': 'xlr3', 'alsoMates': ['jack-635'], 'signals': ['mic-level', 'line-pro']}],
    }));
    File('${d.path}/bad.json').writeAsStringSync(jsonEncode({'connectors': [{'id': 'usb-c', 'label': 'x', 'family': 'usb', 'fit': 'x'}]}));
    final l = Library.load(dir.path);
    expect(l.mates('trs-635', 'motu-combo'), isTrue);
    expect(l.signals, contains('motu-avb'));
    expect(l.problems['bad.json'], 'connector usb-c is built in');
    expect(l.connectors['usb-c']!.label, 'USB Type-C');
  });
}
