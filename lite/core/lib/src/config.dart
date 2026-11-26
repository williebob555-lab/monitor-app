import 'dart:convert';
import 'dart:io';
import 'dart:math';

/// Per-install settings shared by the background program and the app on a
/// computer: this install's device id and where the synced folder is.
class LocalConfig {
  LocalConfig({required this.id, required this.folder, this.name});
  final String id;
  String folder;
  String? name;

  static String get dir {
    final env = Platform.environment;
    if (Platform.isWindows) return '${env['APPDATA'] ?? env['USERPROFILE']}\\MonitorLite';
    final xdg = env['XDG_CONFIG_HOME'];
    return '${xdg != null && xdg.isNotEmpty ? xdg : '${env['HOME']}/.config'}/monitor-lite';
  }

  static String get path => '$dir${Platform.pathSeparator}config.json';

  /// C:\Monitor App\Sync on Windows when C:\Monitor App exists (so its logs
  /// are easy to reach), otherwise a "Monitor" folder in the home folder.
  static String defaultFolder() {
    final env = Platform.environment;
    if (Platform.isWindows) {
      if (Directory(r'C:\Monitor App').existsSync()) return r'C:\Monitor App\Sync';
      return '${env['USERPROFILE']}\\Monitor';
    }
    return '${env['HOME']}/Monitor';
  }

  static String newId() {
    final r = Random.secure();
    return List.generate(6, (_) => r.nextInt(256).toRadixString(16).padLeft(2, '0')).join();
  }

  /// Loads the config, creating it with a fresh id on first run.
  /// MONITOR_FOLDER and MONITOR_NAME, when set, win over the saved values
  /// (used by the Docker image, where the folder is a fixed mount).
  static LocalConfig load() {
    final c = _load();
    final env = Platform.environment;
    final folder = env['MONITOR_FOLDER'], name = env['MONITOR_NAME'];
    if (folder != null && folder.isNotEmpty) c.folder = folder;
    if (name != null && name.isNotEmpty) c.name = name;
    return c;
  }

  static LocalConfig _load() {
    final f = File(path);
    try {
      final j = jsonDecode(f.readAsStringSync());
      if (j is Map && j['id'] is String && j['folder'] is String) {
        return LocalConfig(id: j['id'] as String, folder: j['folder'] as String, name: j['name'] as String?);
      }
    } catch (_) {}
    final c = LocalConfig(id: newId(), folder: defaultFolder());
    c.save();
    return c;
  }

  void save() {
    Directory(dir).createSync(recursive: true);
    final tmp = File('$path.tmp');
    tmp.writeAsStringSync(jsonEncode({'id': id, 'folder': folder, if (name != null) 'name': name}));
    tmp.renameSync(path);
  }
}
