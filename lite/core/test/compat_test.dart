import 'package:monitor_core/monitor_core.dart';
import 'package:test/test.dart';

void main() {
  const cC = Link('USB-C cable', 'usb-c', 'usb-c');

  test('scenario 8: both USB-C, but the monitor has no DisplayPort Alt Mode', () {
    const laptop = Port('laptop USB-C', 'usb-c', dir: 'out', signals: ['usb-2', 'usb-3-10g', 'dp-alt', 'usb-pd-spr']);
    const monitor = Port('monitor USB-C', 'usb-c', dir: 'in', signals: ['usb-2', 'usb-pd-spr']);
    final v = checkChain(laptop, [cC], monitor, want: 'dp-alt');
    expect(v['physical'].verdict, 'ok');
    expect(v['protocol'].verdict, 'fail');
    expect(v['protocol'].reasons.single, contains('monitor USB-C does not support it'));
    expect(v.verdict, 'fail');
    // Same plugs, same cable, a monitor that has it: fine.
    final ok = checkChain(laptop, [cC], const Port('monitor', 'usb-c', dir: 'in', signals: ['dp-alt', 'usb-2']), want: 'dp-alt');
    expect(ok['protocol'].verdict, 'ok');
    expect(ok['protocol'].evidence, 'verified');
  });

  test('scenario 8: 1/4" plugs fit, but speaker level into an instrument input does not', () {
    const amp = Port('amp speaker out', 'ts-635', dir: 'out', signals: ['speaker-level']);
    const pedal = Port('pedal input', 'ts-635', dir: 'in', signals: ['instrument-level']);
    final v = checkChain(amp, [const Link('1/4" cable', 'ts-635', 'ts-635')], pedal);
    expect(v['physical'].verdict, 'ok');
    expect(v['level'].verdict, 'fail');
    expect(v['level'].reasons.single, contains('Speaker level'));
  });

  test('scenario 8: a barrel that fits but gives the wrong voltage and polarity', () {
    const psu = Port('12 V adapter', 'dc-5521', gender: 'plug', dir: 'out', signals: ['dc-12v-cp'], power: {'v': 12, 'polarity': 'center-positive', 'a': 1});
    const pedal = Port('pedal DC in', 'dc-5521', dir: 'in', signals: ['dc-9v-cn'], power: {'v': 9, 'polarity': 'center-negative', 'a': 0.1});
    final v = checkChain(psu, const [], pedal);
    expect(v['physical'].verdict, 'ok');
    expect(v['electrical'].verdict, 'fail');
    expect(v['electrical'].reasons, containsAll([contains('12.0 V'), contains('center-negative')]));
    // The right supply passes.
    final ok = checkChain(const Port('9 V supply', 'dc-5521', gender: 'plug', dir: 'out', power: {'v': 9, 'polarity': 'center-negative', 'a': 0.5}), const [], pedal);
    expect(ok['electrical'].verdict, 'ok');
  });

  test('scenario 8: HDMI to DisplayPort needs an active adapter; the whole path is re-checked', () {
    const gpu = Port('console HDMI', 'hdmi-a', dir: 'out', signals: ['hdmi-2.0']);
    const mon = Port('monitor DP', 'displayport', dir: 'in', signals: ['dp-1.2', 'dp-1.4']);
    final none = checkChain(gpu, const [Link('HDMI cable', 'hdmi-a', 'hdmi-a')], mon);
    expect(none['physical'].verdict, 'fail');
    final passive = checkChain(gpu, const [Link('HDMI cable', 'hdmi-a', 'hdmi-a'), Link('passive adapter', 'hdmi-a', 'displayport', aGender: 'receptacle', passes: ['hdmi-2.0'])], mon);
    expect(passive['physical'].verdict, 'ok');
    expect(passive['protocol'].verdict, 'fail');
    final active = checkChain(gpu, const [Link('HDMI cable', 'hdmi-a', 'hdmi-a'), Link('active converter', 'hdmi-a', 'displayport', aGender: 'receptacle', converts: {'hdmi-2.0': 'dp-1.2'})], mon);
    expect(active.verdict, 'ok');
    expect(active['protocol'].reasons.first, contains('DisplayPort 1.2'));
  });

  test('two outputs facing each other fail; undeclared ports are estimated, not verified', () {
    final v = checkChain(const Port('out A', 'xlr3', dir: 'out', signals: ['line-pro']), const [Link('XLR', 'xlr3', 'xlr3')], const Port('out B', 'xlr3', dir: 'out', signals: ['line-pro']));
    expect(v['level'].verdict, 'fail');
    final u = checkChain(const Port('a', 'usb-c'), const [cC], const Port('b', 'usb-c'));
    expect(u['protocol'].evidence, 'estimated');
    expect(u['electrical'].verdict, 'unknown');
  });

  test('real chain: laptop USB-C to the MOTU interface (declared from specs)', () {
    // Laptop USB-C: USB 3.2 Gen 2 data + charging. MOTU (CueMix 5 family,
    // e.g. UltraLite mk5 / 828): USB-C, USB 2.0 class-compliant audio, own
    // power supply. Re-check against its live thing file (usb.id, speed)
    // once the desktop's things folder is readable.
    const laptop = Port('Wilsons-Laptop USB-C', 'usb-c', dir: 'out', signals: ['usb-2', 'usb-3-10g', 'usb-pd-spr', 'dp-alt']);
    const motu = Port('MOTU USB-C', 'usb-c', dir: 'in', signals: ['usb-2']);
    final v = checkChain(laptop, const [Link('USB-C to C cable (USB 2)', 'usb-c', 'usb-c', passes: ['usb-2', 'usb-c-current'], maxA: 3)], motu, want: 'usb-2');
    expect(v.verdict, 'ok', reason: v.layers.join('\n'));
    expect(v['protocol'].reasons.single, contains('USB 2.0'));
    // A USB-A-only cable can't go in.
    expect(checkChain(laptop, const [Link('A-to-B cable', 'usb-a', 'usb-b')], motu)['physical'].verdict, 'fail');
  });
}
