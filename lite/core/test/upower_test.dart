import 'dart:io';

import 'package:monitor_core/src/agent/hid_ups.dart';
import 'package:monitor_core/src/agent/things.dart';
import 'package:monitor_core/src/agent/upower.dart';
import 'package:monitor_core/src/report.dart';
import 'package:test/test.dart';

/// `upower -i` for the CyberPower OR500 as UPower 1.91 prints it.
String ups({String state = 'fully-charged', String warning = 'none', String pct = '100%', String? empty = '41.0 minutes', String native = 'hiddev1', String serial = '', String present = 'yes'}) => '''
  native-path:          $native
  vendor:               CPS
  model:                UPS OR500
  serial:               $serial
  power supply:         yes
  updated:              Fri 27 Nov 2026 09:30:01 AM EST (12 seconds ago)
  has history:          yes
  ups
    present:             $present
    state:               $state
    warning-level:       $warning
${empty == null ? '' : '    time to empty:       $empty\n'}    percentage:          $pct
    icon-name:          'battery-full-charged-symbolic'
  History (charge):
    1795789800\t100.000\tfully-charged
''';

/// What the exporter writes (packaging/linux/upower-export.sh).
String export(int t, {String body = ''}) => '''exported: $t
Device: /org/freedesktop/UPower/devices/ups_hiddev1
${body.isEmpty ? ups() : body}Daemon:
  daemon-version:  1.91.5
  on-battery:      no
  critical-action: PowerOff
config.UsePercentageForPolicy: true
config.PercentageAction: 20
''';

/// `upower -d` on a laptop that also has a UPS and a wireless mouse.
final dump = '''Device: /org/freedesktop/UPower/devices/battery_BAT0
  native-path:          BAT0
  vendor:               SMP
  model:                5B10W13930
  power supply:         yes
  battery
    present:             yes
    state:               discharging
    percentage:          57%

Device: /org/freedesktop/UPower/devices/mouse_hidpp_battery_0
  native-path:          hidpp_battery_0
  model:                MX Master 3
  power supply:         no
  mouse
    present:             yes
    percentage:          55%

Device: /org/freedesktop/UPower/devices/ups_hiddev1
${ups()}
Device: /org/freedesktop/UPower/devices/DisplayDevice
  power supply:         yes
  updated:              Fri 27 Nov 2026 09:30:01 AM EST (12 seconds ago)
  battery
    present:             yes
    state:               discharging

Daemon:
  daemon-version:  1.91.5
  on-battery:      no
  critical-action: PowerOff
''';

void main() {
  final now = DateTime.utc(2026, 11, 27, 14, 30);
  final t = now.millisecondsSinceEpoch ~/ 1000;
  late Directory dir;
  setUp(() => dir = Directory.systemTemp.createTempSync('upower'));
  tearDown(() => dir.deleteSync(recursive: true));
  File file(String text) => File('${dir.path}/ups.txt')..writeAsStringSync(text);
  Map<String, Reading> byKey(List<Reading> r) => {for (final x in r) x.key: x};

  test('only the UPS counts: never a laptop battery, a mouse or the DisplayDevice', () {
    final u = Upower.parse(dump, from: 'upower');
    expect(u.devices.map((d) => d.kind), ['battery', 'mouse', 'ups', 'battery']);
    expect(u.ups.single.path, endsWith('/ups_hiddev1'));
    expect(u.daemon['critical-action'], 'PowerOff');
  });

  test('a fresh export gives mains, charge, runtime, host protection, each marked as from the host export', () {
    final (u, why) = Upower.readExport(file(export(t - 30)).path, now)!;
    expect(why, isNull);
    final r = byKey(u!.readings(u.ups.single));
    expect(r['ups.status']!.value, 'mains');
    expect(r['ups.charge']!.value, 100);
    expect(r['ups.runtime']!.value, 41);
    expect(r['ups.runtime']!.src, 'estimated');
    expect(r['ups.maker']!.value, 'CPS');
    expect(r['ups.model']!.value, 'UPS OR500');
    expect(r.containsKey('ups.serial'), isFalse, reason: 'an empty serial is not a value');
    expect(r['ups.hostAction']!.value, 'host powers off at 20 %');
    expect(r['ups.hostAction']!.src, 'declared');
    expect(r.values.every((x) => x.from == 'upower-export'), isTrue);
  });

  test('2.6.5 export files (one block, no "Device:" line) still read', () {
    final (u, _) = Upower.readExport(file('exported: $t\n${ups()}').path, now)!;
    expect(byKey(u!.readings(u.ups.single))['ups.charge']!.value, 100);
  });

  test('unavailable is never zero', () {
    Map<String, Reading> read(String body) {
      final u = Upower.parse('Device: /org/freedesktop/UPower/devices/ups_hiddev1\n$body', from: 'upower');
      return byKey(u.readings(u.ups.single));
    }

    // UPower writes 0 for what it does not know; upower leaves the time out.
    var r = read(ups(state: 'unknown', pct: '0%', empty: null));
    for (final k in ['ups.status', 'ups.charge', 'ups.runtime']) {
      expect(r[k]!.value, isNull, reason: k);
      expect(r[k]!.src, 'unknown', reason: k);
      expect(r[k]!.note, isNotEmpty, reason: k);
    }
    r = read(ups(empty: '0.0 seconds'));
    expect(r['ups.runtime']!.value, isNull);
    // A real 0 % while UPower knows the state stays 0.
    r = read(ups(state: 'empty', pct: '0%', warning: 'action'));
    expect(r['ups.charge']!.value, 0);
    expect(r['ups.charge']!.src, 'measured');
    expect(r['ups.status']!.value, 'battery, low');
    r = read(ups(present: 'no'));
    expect(r['ups.status']!.value, isNull);
    expect(r['ups.status']!.note, contains('not present'));
  });

  test('on battery, and low when UPower warns', () {
    Upower p(String body) => Upower.parse('Device: /x/ups_hiddev1\n$body', from: 'upower');
    var u = p(ups(state: 'discharging', pct: '64%'));
    var r = byKey(u.readings(u.ups.single));
    expect(r['ups.status']!.value, 'battery');
    expect(r['ups.status']!.level, 'warn');
    u = p(ups(state: 'discharging', warning: 'critical', pct: '18%'));
    r = byKey(u.readings(u.ups.single));
    expect(r['ups.status']!.value, 'battery, low');
    expect(r['ups.status']!.level, 'fault');
  });

  test('an old export, no UPS, or no file is never shown as current', () {
    expect(Upower.readExport(file(export(t - 600)).path, now)!.$2, contains('10 min old'));
    expect(Upower.readExport(file('exported: $t\nDaemon:\n  daemon-version: 1.91.5\n').path, now)!.$2, 'UPower on the host sees no UPS');
    expect(Upower.readExport(file('garbage').path, now)!.$2, contains('no "exported" time'));
    expect(Upower.readExport('${dir.path}/missing.txt', now), isNull);
  });

  group('matching UPower\'s UPS to the USB device', () {
    test('by following hiddevN in sysfs to its USB port', () {
      final sys = Directory('${dir.path}/sys')..createSync();
      final iface = Directory('${sys.path}/devices/pci0000:00/0000:00:08.1/usb1/1-5/1-5:1.0')..createSync(recursive: true);
      Directory('${sys.path}/class/usbmisc/hiddev1').createSync(recursive: true);
      Link('${sys.path}/class/usbmisc/hiddev1/device').createSync(iface.path);
      final u = Upower.parse(dump, from: 'upower');
      expect(u.match(usbPath: '1-5', usbUpsCount: 2, sys: sys.path)!.$2, 'same USB port');
      expect(u.match(usbPath: '1-6', usbUpsCount: 2, sys: sys.path), isNull);
    });

    test('by a full native-path, serial, maker and model, or as the only UPS', () {
      Upower p(String body) => Upower.parse('Device: /x/ups_hiddev1\n$body', from: 'upower');
      expect(p(ups(native: '/sys/devices/pci0000:00/usb1/1-5/1-5:1.0/usbmisc/hiddev1')).match(usbPath: '1-5', usbUpsCount: 2)!.$2, 'same USB port');
      expect(p(ups(serial: 'CXXLV2004567')).match(usbPath: '9-9', serial: 'cxxlv2004567', usbUpsCount: 2)!.$2, 'same serial number');
      expect(p(ups()).match(usbPath: '9-9', maker: 'CPS', product: 'UPS OR500', usbUpsCount: 2)!.$2, 'same maker and model');
      expect(p(ups()).match(usbPath: '9-9', usbUpsCount: 1)!.$2, 'the only UPS on this machine');
      expect(p(ups()).match(usbPath: '9-9', usbUpsCount: 2), isNull);
    });
  });

  test('UPower leads; USB HID adds the load and alarm; the source and identity say so', () {
    final (u, _) = Upower.readExport(file(export(t)).path, now)!;
    // What HID read on wc-alpha (2026-11-27): mains, 100 %, 70.5 min, 15 % load, alarm on.
    final hid = [
      for (final r in upsReadings({'summary/8500d0': 1, 'summary/850045': 0, 'summary/850066': 100, 'summary/850068': 4230, 'output/840035': 15, 'summary/84005a': 2})) r.withFrom('hid')
    ];
    final r = Things.combine(u!, u.ups.single, 'the only UPS on this machine', vid: '0764', pid: '0601', usbPath: '1-5', hid: hid);
    final k = byKey(r);
    expect(k['ups.runtime']!.value, 41, reason: 'UPower wins where both read it');
    expect(k['ups.runtime']!.from, 'upower-export');
    expect(k['ups.load']!.value, 15);
    expect(k['ups.load']!.from, 'hid');
    expect(k['ups.alarm']!.value, 'on');
    expect(k['ups.source']!.value, 'UPower 1.91.5 on the host (exported file), plus USB HID for load and alarm');
    expect(k['ups.identity']!.value, 'USB 0764:0601 on port 1-5 (it has no serial number)');
    expect(r.first.key, 'ups.status');

    // No HID in the container (wc-desktop): the load is unknown, with why.
    final only = byKey(Things.combine(u, u.ups.single, 'same USB port', vid: '0764', pid: '0601', usbPath: '1-5', hidWhy: '/dev/hidraw1 is not visible here'));
    expect(only['ups.load']!.value, isNull);
    expect(only['ups.load']!.src, 'unknown');
    expect(only['ups.load']!.note, contains('/dev/hidraw1'));
    expect(only.containsKey('ups.alarm'), isFalse);
    expect(only['ups.source']!.value, 'UPower 1.91.5 on the host (exported file)');
  });

  test('the from field survives the file', () {
    final r = Reading(key: 'ups.load', value: null, src: 'unknown', note: 'x', from: 'hid');
    expect(Reading.tryParse(r.toJson())!.from, 'hid');
  });
}
