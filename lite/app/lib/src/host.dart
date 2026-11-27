import 'dart:async';
import 'dart:io';

import 'package:flutter/services.dart';
import 'package:monitor_core/monitor_core.dart';

/// Everything that differs between Android and computers, behind one class.
class LocalHost {
  LocalHost._(this.kind, this.id, this.folder, this.name, this.version, this.tempDir, this.stateDir);

  static const _ch = MethodChannel('monitor/android');

  /// android | windows | linux
  final String kind;
  final String id;
  String folder;
  final String name;
  final String version;
  final String tempDir;

  /// Private to this install, never synced: e.g. which devices are hidden here.
  final String stateDir;

  bool get isAndroid => kind == 'android';

  static Future<LocalHost> load() async {
    if (Platform.isAndroid) {
      final c = (await _ch.invokeMapMethod<String, Object?>('config'))!;
      return LocalHost._('android', c['id']! as String, c['folder']! as String, c['name']! as String,
          appVersion != 'dev' ? appVersion : c['version']! as String, c['cacheDir']! as String, c['filesDir']! as String);
    }
    final c = LocalConfig.load();
    return LocalHost._(Platform.isWindows ? 'windows' : 'linux', c.id, c.folder, c.name ?? Platform.localHostname,
        appVersion, Directory.systemTemp.path, LocalConfig.dir);
  }

  Future<void> setFolder(String path) async {
    folder = path;
    if (isAndroid) {
      await _ch.invokeMethod('setFolder', {'folder': path});
    } else {
      final c = LocalConfig.load()..folder = path;
      c.save();
    }
  }

  /// Android: run one collection now (the app was just opened, or [live]:
  /// the periodic collection while the app is on screen).
  /// Computers: make sure the background program is running.
  Future<void> nudge({bool live = false}) async {
    try {
      if (isAndroid) {
        await _ch.invokeMethod('runNow', {'reason': live ? 'live' : 'app-open'});
      } else {
        await startAgent();
      }
    } catch (_) {}
  }

  String get agentPath {
    final dir = File(Platform.resolvedExecutable).parent.path;
    return Platform.isWindows ? '$dir\\monitor-agent.exe' : '$dir/monitor-agent';
  }

  /// Starts the computer's background program if it isn't already running.
  /// It holds a lock, so a second copy exits at once.
  Future<void> startAgent() async {
    if (Platform.isLinux) {
      final r = await Process.run('systemctl', ['--user', 'start', 'monitor-agent.service']);
      if (r.exitCode == 0) return;
    }
    final p = File(agentPath).existsSync() ? agentPath : (Platform.isLinux ? '/usr/bin/monitor-agent' : agentPath);
    if (File(p).existsSync()) await Process.start(p, const [], mode: ProcessStartMode.detached);
  }

  Future<Map<String, Object?>> androidPermissions() async =>
      isAndroid ? (await _ch.invokeMapMethod<String, Object?>('permissions')) ?? {} : const {};

  Future<({bool on, bool granted})> locationSharing() async {
    if (!isAndroid) return (on: false, granted: false);
    final reply = await _ch.invokeMapMethod<String, Object?>('location');
    return (on: reply?['on'] == true, granted: reply?['granted'] == true);
  }

  Future<void> setLocationSharing(bool on) async {
    if (isAndroid) await _ch.invokeMethod<void>('setLocation', {'on': on});
  }

  /// Android: the devices this phone notifies about, and whether the app may
  /// show notifications at all.
  Future<(Set<String>, bool)> notifyState() async {
    if (!isAndroid) return (<String>{}, false);
    final m = await _ch.invokeMapMethod<String, Object?>('notify') ?? const {};
    return ({...((m['ids'] as List?) ?? const []).whereType<String>()}, m['enabled'] == true);
  }

  Future<void> setNotify(String id, bool on) => _ch.invokeMethod('setNotify', {'id': id, 'on': on});

  Future<void> openAllFiles() => _ch.invokeMethod('openAllFiles');
  Future<void> openBattery() => _ch.invokeMethod('openBattery');
  Future<void> openSyncthing() => _ch.invokeMethod('openSyncthing');

  void log(String line) {
    if (isAndroid) {
      _ch.invokeMethod('log', {'line': 'app: $line'});
    } else {
      SyncFolder(folder).log(id, 'app: $line');
    }
  }

  String get linuxPackage => File('/usr/bin/dnf').existsSync() || File('/usr/bin/rpm').existsSync() && !File('/usr/bin/apt').existsSync() ? 'rpm' : 'deb';

  /// Hands a downloaded update to the system installer. On computers the app
  /// closes itself so the installer can replace it; the installer restarts it.
  /// Android: returns "permission" when Install unknown apps is off for the
  /// app (its settings page has been opened), else "started".
  Future<String?> install(File f) async {
    log('installing ${f.path}');
    if (isAndroid) {
      return _ch.invokeMethod<String>('installApk', {'path': f.path});
    } else if (Platform.isWindows) {
      await Process.start(f.path, ['/SILENT', '/SUPPRESSMSGBOXES', '/NORESTART'], mode: ProcessStartMode.detached);
      exit(0);
    } else {
      final args = f.path.endsWith('.rpm') ? ['dnf', 'install', '-y', f.path] : ['apt-get', 'install', '-y', f.path];
      final r = await Process.run('pkexec', args);
      if (r.exitCode != 0) throw StateError('The installer failed: ${(r.stderr as String).trim()}');
      await Process.run('systemctl', ['--user', 'restart', 'monitor-agent.service']);
    }
    return 'started';
  }
}
