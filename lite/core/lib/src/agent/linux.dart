import 'dart:ffi';
import 'dart:io';

import 'package:crypto/crypto.dart';
import 'package:ffi/ffi.dart';

import '../report.dart';
import 'sample.dart';

/// Reads /proc and /sys. No processes are started except `ping`.
class LinuxHost implements Host {
  @override
  String get kind => 'linux';

  List<int>? _cpuPrev;
  (int, int, DateTime)? _netPrev;

  String? _read(String p) {
    try {
      return File(p).readAsStringSync().trim();
    } catch (_) {
      return null;
    }
  }

  @override
  String osName() {
    final rel = _read('/etc/os-release') ?? '';
    final m = RegExp(r'^PRETTY_NAME="?([^"\n]*)"?', multiLine: true).firstMatch(rel);
    final name = m?.group(1) ?? 'Linux';
    return File('/.dockerenv').existsSync() ? '$name (in Docker)' : name;
  }

  /// Lowest permanent MAC of a physical network card: the same on every OS
  /// installed on this machine, so dual-boot installs are recognised.
  @override
  String? machineKey() {
    try {
      final macs = <String>[];
      for (final e in Directory('/sys/class/net').listSync()) {
        final name = e.uri.pathSegments.where((s) => s.isNotEmpty).last;
        if (!Directory('/sys/class/net/$name/device').existsSync()) continue;
        // Wired and Wi-Fi cards only (type 1), as on Windows.
        if (_read('/sys/class/net/$name/type')?.trim() != '1') continue;
        final mac = _read('/sys/class/net/$name/address')?.trim().toLowerCase();
        if (mac == null || mac == '00:00:00:00:00:00') continue;
        // A random or set address (NetworkManager can randomise Wi-Fi) is
        // not the card's own: use the burned-in one ethtool reports, if any.
        final assigned = _read('/sys/class/net/$name/addr_assign_type')?.trim();
        if (assigned != null && assigned != '0') {
          final perm = _permanentMac(name);
          if (perm != null) macs.add(perm);
          continue;
        }
        if (int.parse(mac.substring(0, 2), radix: 16) & 2 != 0) continue; // locally administered
        macs.add(mac);
      }
      if (macs.isEmpty) return null;
      macs.sort();
      return sha256.convert(macs.first.codeUnits).toString().substring(0, 16);
    } catch (_) {
      return null;
    }
  }

  /// The card's burned-in address via `ethtool -P`, when ethtool is there.
  static String? _permanentMac(String dev) {
    try {
      final r = Process.runSync('ethtool', ['-P', dev]);
      final m = RegExp(r'([0-9a-f]{2}(:[0-9a-f]{2}){5})').firstMatch('${r.stdout}'.toLowerCase());
      final mac = m?.group(1);
      return mac == null || mac == '00:00:00:00:00:00' ? null : mac;
    } catch (_) {
      return null;
    }
  }

  @override
  Sample sample() {
    final s = Sample();
    // CPU: delta of /proc/stat totals since the last sample.
    final stat = _read('/proc/stat');
    if (stat != null) {
      final first = stat.split('\n').first.split(RegExp(r'\s+')).skip(1).map(int.tryParse).whereType<int>().toList();
      if (first.length >= 4) {
        final idle = first[3] + (first.length > 4 ? first[4] : 0);
        final total = first.fold<int>(0, (a, b) => a + b);
        final prev = _cpuPrev;
        if (prev != null && total > prev[1]) {
          s.cpuPct = 100 * (1 - (idle - prev[0]) / (total - prev[1]));
        }
        _cpuPrev = [idle, total];
      }
      final bt = RegExp(r'^btime (\d+)', multiLine: true).firstMatch(stat);
      if (bt != null) s.bootTime = DateTime.fromMillisecondsSinceEpoch(int.parse(bt.group(1)!) * 1000, isUtc: true);
    }
    // Memory
    final mi = _read('/proc/meminfo');
    if (mi != null) {
      int? kb(String k) => int.tryParse(RegExp('^$k:\\s+(\\d+)', multiLine: true).firstMatch(mi)?.group(1) ?? '');
      final total = kb('MemTotal'), avail = kb('MemAvailable');
      if (total != null && avail != null) {
        s.memTotal = total * 1024;
        s.memUsed = (total - avail) * 1024;
      }
    }
    // Disk: statvfs("/"), or MONITOR_DISK_PATH (in Docker "/" is the container).
    final d = _statvfs(Platform.environment['MONITOR_DISK_PATH'] ?? '/');
    if (d != null) {
      s.diskTotal = d.$1;
      s.diskUsed = d.$1 - d.$2;
    }
    // Battery
    try {
      for (final e in Directory('/sys/class/power_supply').listSync()) {
        final p = e.path;
        final type = _read('$p/type');
        if (type == 'Battery' && _read('$p/scope') != 'Device') {
          final cap = double.tryParse(_read('$p/capacity') ?? '');
          final status = _read('$p/status');
          s.battery = Battery(pct: cap, charging: status == 'Charging', plugged: status != 'Discharging');
        }
      }
    } catch (_) {}
    // Network: sum of real interfaces in /proc/net/dev.
    final nd = _read('/proc/net/dev');
    if (nd != null) {
      var rx = 0, tx = 0;
      var wifi = false, any = false;
      for (final line in nd.split('\n').skip(2)) {
        final parts = line.trim().split(RegExp(r'[:\s]+'));
        if (parts.length < 10) continue;
        final name = parts[0];
        if (name == 'lo' || RegExp(r'^(tailscale|docker|veth|br-|virbr|tun|wg)').hasMatch(name)) continue;
        if (_read('/sys/class/net/$name/operstate') != 'up') continue;
        any = true;
        if (name.startsWith('wl')) wifi = true;
        rx += int.tryParse(parts[1]) ?? 0;
        tx += int.tryParse(parts[9]) ?? 0;
      }
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
    }
    return s;
  }

  (int, int)? _statvfs(String path) {
    if (!(Abi.current() == Abi.linuxX64 || Abi.current() == Abi.linuxArm64)) return null;
    try {
      final libc = DynamicLibrary.process();
      final fn = libc.lookupFunction<Int32 Function(Pointer<Utf8>, Pointer<Uint8>), int Function(Pointer<Utf8>, Pointer<Uint8>)>('statvfs');
      final p = path.toNativeUtf8();
      final buf = calloc<Uint8>(256);
      try {
        if (fn(p, buf) != 0) return null;
        final b = buf.cast<Uint64>();
        final frsize = b[1], blocks = b[2], bavail = b[4];
        return (blocks * frsize, bavail * frsize);
      } finally {
        calloc.free(p);
        calloc.free(buf);
      }
    } catch (_) {
      return null;
    }
  }

  @override
  int? processCpuMs() {
    final s = _read('/proc/self/stat');
    if (s == null) return null;
    final f = s.substring(s.lastIndexOf(')') + 2).split(' ');
    // Fields after the command name: state is [0], utime [11], stime [12].
    final ut = int.tryParse(f[11]), st = int.tryParse(f[12]);
    if (ut == null || st == null) return null;
    return (ut + st) * 10; // USER_HZ is 100 on every Linux we target
  }

  @override
  Future<int?> ping(String ip) async {
    try {
      final r = await Process.run('ping', ['-c', '1', '-W', '2', '-n', ip]).timeout(const Duration(seconds: 4));
      if (r.exitCode != 0) return null;
      final m = RegExp(r'time[=<]([\d.]+)').firstMatch(r.stdout as String);
      return m == null ? 0 : double.parse(m.group(1)!).round();
    } catch (_) {
      return null;
    }
  }
}

/// Size and free bytes of the filesystem holding [path].
(int, int)? linuxStatvfs(String path) => LinuxHost()._statvfs(path);
