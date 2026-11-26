import 'dart:ffi';
import 'dart:io';
import 'dart:math' as math;

import 'package:ffi/ffi.dart';

import '../report.dart';

/// Reads a USB UPS directly, the way the UPS describes itself: every USB UPS
/// (CyberPower, APC, Eaton...) follows the USB "HID Power Device" standard.
/// Its report descriptor (readable in sysfs) says which numbers it offers;
/// the numbers come from /dev/hidrawN with a read-only "get feature report"
/// call. No vendor software, no NUT, no upower.
class HidUps {
  /// Readings for the UPS at USB [usbPath] (e.g. "1-4"), or null when it
  /// can't be read here, with [why] set to the reason.
  static List<Reading>? read(String usbPath, void Function(String why) why) {
    final raw = _hidraw(usbPath);
    if (raw == null) {
      why('no hidraw device for it (the kernel did not attach its HID driver)');
      return null;
    }
    List<int> desc;
    try {
      desc = File('/sys/class/hidraw/$raw/device/report_descriptor').readAsBytesSync();
    } catch (e) {
      why('could not read its report descriptor: $e');
      return null;
    }
    final fields = parseDescriptor(desc);
    if (fields.isEmpty) {
      why('its report descriptor lists no values');
      return null;
    }
    // Inside Docker the device is passed in under a fixed name.
    final dev = [for (final p in ['/dev/$raw', '/dev/monitor-ups']) if (File(p).existsSync()) p].firstOrNull;
    if (dev == null) {
      why('/dev/$raw is not visible here; see "UPS" in packaging/docker/README.md');
      return null;
    }
    final values = _readFeatures(dev, fields, why);
    if (values == null) return null;
    return upsReadings(values);
  }

  static String? _hidraw(String usbPath) {
    try {
      for (final e in Directory('/sys/class/hidraw').listSync()) {
        final name = e.uri.pathSegments.where((x) => x.isNotEmpty).last;
        final target = Link('${e.path}/device').resolveSymbolicLinksSync();
        if (target.contains('/$usbPath:')) return name;
      }
    } catch (_) {}
    return null;
  }

  /// Usage key (page << 16 | id) in its context -> value, from every
  /// feature report the descriptor lists.
  static Map<String, double>? _readFeatures(String dev, List<HidField> fields, void Function(String) why) {
    final libc = DynamicLibrary.open('libc.so.6');
    final open = libc.lookupFunction<Int32 Function(Pointer<Utf8>, Int32), int Function(Pointer<Utf8>, int)>('open');
    final close = libc.lookupFunction<Int32 Function(Int32), int Function(int)>('close');
    final ioctl = libc.lookupFunction<Int32 Function(Int32, UnsignedLong, VarArgs<(Pointer<Uint8>,)>), int Function(int, int, Pointer<Uint8>)>('ioctl');
    final path = dev.toNativeUtf8();
    final fd = open(path, 0); // O_RDONLY
    malloc.free(path);
    if (fd < 0) {
      why('no permission to read $dev; see "UPS" in packaging/linux/README.md (one udev rule)');
      return null;
    }
    final out = <String, double>{};
    final buf = malloc<Uint8>(512);
    try {
      final byReport = <int, List<HidField>>{};
      for (final f in fields.where((f) => f.feature)) {
        byReport.putIfAbsent(f.reportId, () => []).add(f);
      }
      for (final e in byReport.entries) {
        final bits = e.value.map((f) => f.bitOffset + f.size).reduce(math.max);
        final len = (bits + 7) ~/ 8 + 1;
        if (len > 512) continue;
        buf[0] = e.key;
        // HIDIOCGFEATURE(len) = _IOC(read|write, 'H', 0x07, len)
        final req = (3 << 30) | (len << 16) | (0x48 << 8) | 0x07;
        final n = ioctl(fd, req, buf);
        if (n < 0) continue;
        final bytes = buf.asTypedList(len);
        // The kernel puts the report id in byte 0 when the device uses ids.
        final data = e.key == 0 ? bytes : bytes.sublist(1);
        for (final f in e.value) {
          final v = f.decode(data);
          if (v != null) out.putIfAbsent(f.key, () => v);
        }
      }
    } finally {
      malloc.free(buf);
      close(fd);
    }
    if (out.isEmpty) why('it answered no feature reports');
    return out.isEmpty ? null : out;
  }
}

/// One value in a HID report.
class HidField {
  HidField(this.feature, this.reportId, this.bitOffset, this.size, this.usage, this.context, this.logicalMin, this.logicalMax, this.constant);
  final bool feature;
  final int reportId, bitOffset, size, usage, logicalMin, logicalMax;

  /// The nearest enclosing power-device collection: input, output, battery
  /// or summary. Tells an input voltage from a battery voltage.
  final String context;
  final bool constant;

  String get key => '$context/${usage.toRadixString(16)}';

  double? decode(List<int> data) {
    if (constant || size == 0 || size > 32) return null;
    var v = 0;
    for (var i = 0; i < size; i++) {
      final bit = bitOffset + i;
      if (bit ~/ 8 >= data.length) return null;
      if ((data[bit ~/ 8] >> (bit % 8)) & 1 == 1) v |= 1 << i;
    }
    if (logicalMin < 0 && (v >> (size - 1)) & 1 == 1) v -= 1 << size;
    return v.toDouble();
  }
}

const _collectionNames = {0x84001a: 'input', 0x84001c: 'output', 0x840012: 'battery', 0x840010: 'battery', 0x840024: 'summary'};

/// Parses a HID report descriptor into its input and feature values.
List<HidField> parseDescriptor(List<int> d) {
  final out = <HidField>[];
  var page = 0, lmin = 0, lmax = 0, size = 0, count = 0, reportId = 0;
  final stack = <List<int>>[];
  var usages = <int>[];
  int? umin, umax;
  final offsets = <String, int>{};
  final ctx = <String>[];
  int sval(int at, int n) {
    var v = 0;
    for (var i = 0; i < n; i++) {
      v |= d[at + i] << (8 * i);
    }
    if (n > 0 && n < 4 && (v >> (8 * n - 1)) & 1 == 1) v -= 1 << (8 * n);
    if (n == 4 && v >= 0x80000000) v -= 0x100000000;
    return v;
  }

  int uval(int at, int n) {
    var v = 0;
    for (var i = 0; i < n; i++) {
      v |= d[at + i] << (8 * i);
    }
    return v;
  }

  var i = 0;
  while (i < d.length) {
    final p = d[i];
    if (p == 0xfe) {
      if (i + 1 >= d.length) break;
      i += 3 + d[i + 1];
      continue;
    }
    final n = const [0, 1, 2, 4][p & 3];
    if (i + 1 + n > d.length) break;
    final type = (p >> 2) & 3, tag = p >> 4;
    final u = uval(i + 1, n), s = sval(i + 1, n);
    int full(int x) => n == 4 ? x : (page << 16) | x;
    switch ((type, tag)) {
      case (1, 0):
        page = u;
      case (1, 1):
        lmin = s;
      case (1, 2):
        lmax = lmin < 0 ? s : u;
      case (1, 7):
        size = u;
      case (1, 8):
        reportId = u;
      case (1, 9):
        count = u;
      case (1, 10):
        stack.add([page, lmin, lmax, size, count, reportId]);
      case (1, 11):
        if (stack.isNotEmpty) {
          final g = stack.removeLast();
          page = g[0];
          lmin = g[1];
          lmax = g[2];
          size = g[3];
          count = g[4];
          reportId = g[5];
        }
      case (2, 0):
        usages.add(full(u));
      case (2, 1):
        umin = full(u);
      case (2, 2):
        umax = full(u);
      case (0, 10): // collection
        final c = usages.isNotEmpty ? usages.first : 0;
        ctx.add(_collectionNames[c] ?? (ctx.isEmpty ? '' : ctx.last));
        usages = [];
        umin = umax = null;
      case (0, 12): // end collection
        if (ctx.isNotEmpty) ctx.removeLast();
      case (0, 8) || (0, 9) || (0, 11): // input, output, feature
        final kind = tag == 8 ? 'in' : tag == 9 ? 'out' : 'feat';
        final k = '$kind/$reportId';
        var off = offsets[k] ?? 0;
        final constant = u & 1 == 1;
        if (kind != 'out') {
          for (var c = 0; c < count; c++) {
            final usage = c < usages.length
                ? usages[c]
                : umin != null && umax != null
                    ? math.min(umin + c, umax)
                    : usages.isNotEmpty
                        ? usages.last
                        : 0;
            out.add(HidField(kind == 'feat', reportId, off + c * size, size, usage, ctx.isEmpty ? '' : ctx.last, lmin, lmax, constant || usage == 0));
          }
        }
        off += size * count;
        offsets[k] = off;
        usages = [];
        umin = umax = null;
    }
    i += 1 + n;
  }
  return out;
}

/// The basic status only: on mains or battery, charge, load and runtime.
/// Trends come from history in the app, not from more fields here.
List<Reading> upsReadings(Map<String, double> v) {
  double? any(int usage, [List<String> ctx = const ['summary', 'battery', 'input', 'output', '']]) {
    for (final c in ctx) {
      final x = v['$c/${usage.toRadixString(16)}'];
      if (x != null) return x;
    }
    return null;
  }

  bool? flag(int usage) => any(usage) == null ? null : any(usage)! != 0;

  final ac = flag(0x8500d0);
  final discharging = flag(0x850045);
  final low = flag(0x850042) ?? flag(0x840069);
  final onBattery = discharging == true || ac == false;
  final charge = any(0x850066);
  final runtime = any(0x850068);
  final load = any(0x840035, const ['output', 'summary', '', 'input']);
  final unknown = 'the UPS does not report it';
  Reading num(String key, String label, double? x, String unit, {String src = 'measured', String? note, String? level}) =>
      Reading(key: key, label: label, value: x == null ? null : double.parse(x.toStringAsFixed(1)), unit: unit, group: 'Power',
          src: x == null ? 'unknown' : src, note: x == null ? unknown : note, level: level);
  return [
    Reading(
        key: 'ups.status',
        label: 'Running on',
        value: low == true && onBattery ? 'battery, low' : onBattery ? 'battery' : ac == null && discharging == null ? null : 'mains',
        group: 'Power',
        src: ac == null && discharging == null ? 'unknown' : 'measured',
        note: ac == null && discharging == null ? unknown : null,
        level: low == true && onBattery ? 'fault' : onBattery ? 'warn' : null),
    num('ups.charge', 'Battery charge', charge, '%', level: charge == null ? null : charge < 20 ? 'fault' : charge < 50 && onBattery ? 'warn' : null),
    num('ups.runtime', 'Runtime left', runtime == null ? null : runtime / 60, 'min', src: 'estimated', note: "the UPS's own estimate at the current load"),
    num('ups.load', 'Load', load, '%'),
  ];
}
