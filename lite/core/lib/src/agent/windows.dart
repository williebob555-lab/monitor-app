import 'dart:ffi';
import 'dart:io';
import 'dart:isolate';

import 'package:crypto/crypto.dart';
import 'package:ffi/ffi.dart';

import '../report.dart';
import 'sample.dart';

// Win32 calls through FFI only: no child processes, so no console windows
// ever flash, and nothing heavier than a few system calls per minute.

final _k32 = DynamicLibrary.open('kernel32.dll');
final _iph = DynamicLibrary.open('iphlpapi.dll');

final _getSystemTimes = _k32.lookupFunction<Int32 Function(Pointer<Uint64>, Pointer<Uint64>, Pointer<Uint64>),
    int Function(Pointer<Uint64>, Pointer<Uint64>, Pointer<Uint64>)>('GetSystemTimes');
final _globalMemoryStatusEx =
    _k32.lookupFunction<Int32 Function(Pointer<Uint8>), int Function(Pointer<Uint8>)>('GlobalMemoryStatusEx');
final _getDiskFreeSpaceEx = _k32.lookupFunction<Int32 Function(Pointer<Utf16>, Pointer<Uint64>, Pointer<Uint64>, Pointer<Uint64>),
    int Function(Pointer<Utf16>, Pointer<Uint64>, Pointer<Uint64>, Pointer<Uint64>)>('GetDiskFreeSpaceExW');
final _getSystemPowerStatus =
    _k32.lookupFunction<Int32 Function(Pointer<Uint8>), int Function(Pointer<Uint8>)>('GetSystemPowerStatus');
final _getCurrentProcess = _k32.lookupFunction<IntPtr Function(), int Function()>('GetCurrentProcess');
final _getProcessTimes = _k32.lookupFunction<
    Int32 Function(IntPtr, Pointer<Uint64>, Pointer<Uint64>, Pointer<Uint64>, Pointer<Uint64>),
    int Function(int, Pointer<Uint64>, Pointer<Uint64>, Pointer<Uint64>, Pointer<Uint64>)>('GetProcessTimes');
final _getTickCount64 = _k32.lookupFunction<Uint64 Function(), int Function()>('GetTickCount64');
final _getIfTable2 =
    _iph.lookupFunction<Uint32 Function(Pointer<Pointer<Uint8>>), int Function(Pointer<Pointer<Uint8>>)>('GetIfTable2');
final _freeMibTable = _iph.lookupFunction<Void Function(Pointer<Uint8>), void Function(Pointer<Uint8>)>('FreeMibTable');
final _icmpCreateFile = _iph.lookupFunction<IntPtr Function(), int Function()>('IcmpCreateFile');
final _icmpCloseHandle = _iph.lookupFunction<Int32 Function(IntPtr), int Function(int)>('IcmpCloseHandle');
final _icmpSendEcho = _iph.lookupFunction<
    Uint32 Function(IntPtr, Uint32, Pointer<Uint8>, Uint16, Pointer<Void>, Pointer<Uint8>, Uint32, Uint32),
    int Function(int, int, Pointer<Uint8>, int, Pointer<Void>, Pointer<Uint8>, int, int)>('IcmpSendEcho');

// MIB_IF_ROW2 layout on 64-bit Windows (sizeof = 1352).
const _rowSize = 1352;
const _oDescription = 542; // WCHAR[257], after Alias
const _oPhysLen = 1056, _oPermPhys = 1092, _oType = 1128, _oFlags = 1152, _oOper = 1156;
const _oInOctets = 1208, _oOutOctets = 1280;

class WindowsHost implements Host {
  @override
  String get kind => 'windows';

  (int, int)? _cpuPrev;
  (int, int, DateTime)? _netPrev;

  @override
  String osName() {
    // e.g. '"Windows 10 Pro" 10.0 (Build 26100)'; builds from 22000 are Windows 11.
    final v = Platform.operatingSystemVersion;
    final build = int.tryParse(RegExp(r'Build (\d+)').firstMatch(v)?.group(1) ?? '') ?? 0;
    var name = RegExp(r'"([^"]+)"').firstMatch(v)?.group(1) ?? 'Windows';
    if (build >= 22000) name = name.replaceFirst('Windows 10', 'Windows 11');
    return name;
  }

  /// Iterates real network adapters (hardware, not filter drivers).
  void _rows(void Function(Pointer<Uint8> row) each) {
    final pp = calloc<Pointer<Uint8>>();
    try {
      if (_getIfTable2(pp) != 0) return;
      final table = pp.value;
      try {
        final n = table.cast<Uint32>().value;
        for (var i = 0; i < n; i++) {
          final row = table + 8 + i * _rowSize;
          final flags = row[_oFlags];
          final hardware = flags & 1 != 0, filter = flags & 2 != 0;
          if (hardware && !filter) each(row);
        }
      } finally {
        _freeMibTable(table);
      }
    } finally {
      calloc.free(pp);
    }
  }

  /// Lowest burned-in MAC of a wired or Wi-Fi card, the same rule as on
  /// Linux, so a laptop that dual-boots Windows and Linux is recognised as
  /// one machine. Bluetooth (which Linux does not list as a network card)
  /// and locally administered (random or virtual) addresses are left out.
  @override
  String? machineKey() {
    final macs = <String>[];
    _rows((row) {
      final len = (row + _oPhysLen).cast<Uint32>().value;
      if (len != 6) return;
      final type = (row + _oType).cast<Uint32>().value;
      if (type != 6 && type != 71) return; // Ethernet, Wi-Fi
      final desc = (row + _oDescription).cast<Utf16>().toDartString();
      if (desc.toLowerCase().contains('bluetooth')) return;
      if (row[_oPermPhys] & 2 != 0) return; // locally administered
      final mac = List.generate(6, (i) => row[_oPermPhys + i].toRadixString(16).padLeft(2, '0')).join(':');
      if (mac != '00:00:00:00:00:00') macs.add(mac);
    });
    if (macs.isEmpty) return null;
    macs.sort();
    return sha256.convert(macs.first.codeUnits).toString().substring(0, 16);
  }

  @override
  Sample sample() {
    final s = Sample();
    final a = calloc<Uint64>(4);
    try {
      if (_getSystemTimes(a, a + 1, a + 2) != 0) {
        final idle = a[0], busy = a[1] + a[2]; // kernel time includes idle
        final prev = _cpuPrev;
        if (prev != null && busy > prev.$2) {
          s.cpuPct = 100 * (1 - (idle - prev.$1) / (busy - prev.$2));
        }
        _cpuPrev = (idle, busy);
      }
      final root = '${Platform.environment['SystemDrive'] ?? 'C:'}\\'.toNativeUtf16();
      if (_getDiskFreeSpaceEx(root, a, a + 1, a + 2) != 0) {
        s.diskTotal = a[1];
        s.diskUsed = a[1] - a[2];
      }
      calloc.free(root);
    } finally {
      calloc.free(a);
    }
    final m = calloc<Uint8>(64);
    try {
      m.cast<Uint32>().value = 64;
      if (_globalMemoryStatusEx(m) != 0) {
        final q = m.cast<Uint64>();
        s.memTotal = q[1];
        s.memUsed = q[1] - q[2];
      }
    } finally {
      calloc.free(m);
    }
    final p = calloc<Uint8>(12);
    try {
      if (_getSystemPowerStatus(p) != 0) {
        final ac = p[0], flag = p[1], pct = p[2];
        if (flag != 128 && flag != 255 && pct != 255) {
          s.battery = Battery(pct: pct.toDouble(), charging: flag & 8 != 0, plugged: ac == 1);
        }
      }
    } finally {
      calloc.free(p);
    }
    s.bootTime = DateTime.now().toUtc().subtract(Duration(milliseconds: _getTickCount64()));
    var rx = 0, tx = 0;
    var wifi = false, any = false;
    _rows((row) {
      if ((row + _oOper).cast<Uint32>().value != 1) return; // IfOperStatusUp
      any = true;
      if ((row + _oType).cast<Uint32>().value == 71) wifi = true;
      rx += (row + _oInOctets).cast<Uint64>().value;
      tx += (row + _oOutOctets).cast<Uint64>().value;
    });
    final now = DateTime.now();
    final prev = _netPrev;
    if (prev != null) {
      final secs = now.difference(prev.$3).inMilliseconds / 1000;
      if (secs > 0 && rx >= prev.$1 && tx >= prev.$2) {
        s.netRxBps = (rx - prev.$1) / secs;
        s.netTxBps = (tx - prev.$2) / secs;
      }
    }
    _netPrev = (rx, tx, now);
    s.netType = any ? (wifi ? 'wifi' : 'ethernet') : 'none';
    return s;
  }

  @override
  int? processCpuMs() {
    final t = calloc<Uint64>(4);
    try {
      if (_getProcessTimes(_getCurrentProcess(), t, t + 1, t + 2, t + 3) == 0) return null;
      return (t[2] + t[3]) ~/ 10000; // 100 ns units
    } finally {
      calloc.free(t);
    }
  }

  @override
  String? get pingProblem => null;

  /// IcmpSendEcho blocks its thread for up to 2 s per unreachable device.
  /// Called on the collector's own isolate it froze the whole cycle, one
  /// timeout after another: the laptop's 8.9 s cycles on 2026-11-27 with
  /// the Pixel, wc-alpha and wc-laptop-f unreachable (cpu 16 ms, all
  /// waiting). Each ping now runs on its own short-lived isolate, so they
  /// overlap with each other and with the TCP probes.
  @override
  Future<int?> ping(String ip) => Isolate.run(() => _pingBlocking(ip));
}

int? _pingBlocking(String ip) {
  final parts = ip.split('.').map(int.tryParse).toList();
  if (parts.length != 4 || parts.any((x) => x == null)) return null;
  // IPAddr is in network byte order; on little-endian that's a reversed int.
  final addr = parts[0]! | parts[1]! << 8 | parts[2]! << 16 | parts[3]! << 24;
  final h = _icmpCreateFile();
  if (h == -1 || h == 0) return null;
  final data = calloc<Uint8>(8);
  final reply = calloc<Uint8>(256);
  try {
    final n = _icmpSendEcho(h, addr, data, 8, nullptr, reply, 256, 2000);
    if (n == 0) return null;
    final r = reply.cast<Uint32>();
    return r[1] == 0 ? r[2] : null; // Status 0 = IP_SUCCESS, then RoundTripTime
  } finally {
    calloc.free(data);
    calloc.free(reply);
    _icmpCloseHandle(h);
  }
}

final _adv = DynamicLibrary.open('advapi32.dll');
final _regGetValue = _adv.lookupFunction<
    Int32 Function(IntPtr, Pointer<Utf16>, Pointer<Utf16>, Uint32, Pointer<Uint32>, Pointer<Void>, Pointer<Uint32>),
    int Function(int, Pointer<Utf16>, Pointer<Utf16>, int, Pointer<Uint32>, Pointer<Void>, Pointer<Uint32>)>('RegGetValueW');
const _hklm = 0x80000002;
const _rrfRtRegSz = 0x00000002;

/// A text value from HKEY_LOCAL_MACHINE, read in-process (no reg.exe).
String? windowsRegString(String key, String value) {
  final k = key.toNativeUtf16(), v = value.toNativeUtf16();
  final size = calloc<Uint32>()..value = 1024;
  final buf = calloc<Uint8>(1024);
  try {
    if (_regGetValue(_hklm, k, v, _rrfRtRegSz, nullptr, buf.cast(), size) != 0) return null;
    final s = buf.cast<Utf16>().toDartString().trim();
    return s.isEmpty ? null : s;
  } finally {
    calloc.free(k);
    calloc.free(v);
    calloc.free(size);
    calloc.free(buf);
  }
}

final _getLogicalDrives = _k32.lookupFunction<Uint32 Function(), int Function()>('GetLogicalDrives');
final _getDriveType = _k32.lookupFunction<Uint32 Function(Pointer<Utf16>), int Function(Pointer<Utf16>)>('GetDriveTypeW');

/// Fixed and removable drives: (letter, total bytes, free bytes).
List<(String, int, int)> windowsDrives() {
  final out = <(String, int, int)>[];
  final mask = _getLogicalDrives();
  final a = calloc<Uint64>(3);
  try {
    for (var i = 2; i < 26; i++) {
      if (mask & (1 << i) == 0) continue;
      final letter = '${String.fromCharCode(65 + i)}:';
      final root = '$letter\\'.toNativeUtf16();
      try {
        final t = _getDriveType(root);
        if (t != 2 && t != 3) continue; // removable, fixed
        if (_getDiskFreeSpaceEx(root, a, a + 1, a + 2) != 0 && a[1] > 0) out.add((letter, a[1], a[2]));
      } finally {
        calloc.free(root);
      }
    }
  } finally {
    calloc.free(a);
  }
  return out;
}

final _createFileW = _k32.lookupFunction<IntPtr Function(Pointer<Utf16>, Uint32, Uint32, Pointer<Void>, Uint32, Uint32, IntPtr),
    int Function(Pointer<Utf16>, int, int, Pointer<Void>, int, int, int)>('CreateFileW');
final _writeFile = _k32.lookupFunction<Int32 Function(IntPtr, Pointer<Uint8>, Uint32, Pointer<Uint32>, Pointer<Void>),
    int Function(int, Pointer<Uint8>, int, Pointer<Uint32>, Pointer<Void>)>('WriteFile');
final _readFile = _k32.lookupFunction<Int32 Function(IntPtr, Pointer<Uint8>, Uint32, Pointer<Uint32>, Pointer<Void>),
    int Function(int, Pointer<Uint8>, int, Pointer<Uint32>, Pointer<Void>)>('ReadFile');
final _closeHandle = _k32.lookupFunction<Int32 Function(IntPtr), int Function(int)>('CloseHandle');
final _getLastError = _k32.lookupFunction<Uint32 Function(), int Function()>('GetLastError');

/// Sends [request] down a named pipe and reads until the other side closes
/// it (blocking: call it on a side isolate). Throws with the Win32 error.
List<int> windowsPipeRequest(String path, List<int> request, {int maxBytes = 4 * 1024 * 1024}) {
  const genericRw = 0xC0000000, openExisting = 3, invalid = -1;
  final p = path.toNativeUtf16();
  final h = _createFileW(p, genericRw, 0, nullptr, openExisting, 0, 0);
  calloc.free(p);
  if (h == invalid || h == 0) throw OSError('cannot open $path', _getLastError());
  final buf = calloc<Uint8>(64 * 1024);
  final n = calloc<Uint32>();
  try {
    final req = calloc<Uint8>(request.length);
    req.asTypedList(request.length).setAll(0, request);
    final ok = _writeFile(h, req, request.length, n, nullptr);
    calloc.free(req);
    if (ok == 0) throw OSError('cannot write to $path', _getLastError());
    final out = <int>[];
    while (out.length < maxBytes) {
      if (_readFile(h, buf, 64 * 1024, n, nullptr) == 0 || n.value == 0) break; // closed: done
      out.addAll(buf.asTypedList(n.value));
    }
    return out;
  } finally {
    calloc.free(buf);
    calloc.free(n);
    _closeHandle(h);
  }
}
