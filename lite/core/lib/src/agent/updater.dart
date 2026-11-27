import 'dart:io';

import '../config.dart';
import '../control.dart';
import '../folder.dart';
import '../update.dart';

/// "Update now", asked from any device (control "update").
///
/// Windows: the collector downloads the newest release (checked against its
/// SHA256SUMS) and runs the per-user installer silently, which restarts it.
/// Linux (package or Docker): installing needs root, which the collector
/// never has. It leaves a flag file that a root systemd path unit watches
/// (installed by the .rpm/.deb, or by packaging/docker/update.sh --install);
/// that unit runs the same checked update as the nightly one.
/// Android can't install without a tap on the phone; its collector posts a
/// notification instead (Collector.kt).
class Updater {
  /// Docker: the private state folder (/data), where update.sh --install
  /// leaves a marker that its path unit is watching.
  static const _dockerFlagDir = '/data';
  static const _packageFlagDir = '/var/lib/monitor-app/requests';

  static bool get _docker => File('/.dockerenv').existsSync();

  static String? get _flagDir {
    if (!Platform.isLinux) return null;
    if (_docker) return File('$_dockerFlagDir/.update-watched').existsSync() ? _dockerFlagDir : null;
    return Directory(_packageFlagDir).existsSync() ? _packageFlagDir : null;
  }

  static bool get canUpdate => Platform.isWindows || _flagDir != null;

  /// Windows: the installer stops this process, so it leaves first.
  static bool get exitsAfterStart => Platform.isWindows;

  static Control control(String version) => Control(
        key: 'update',
        label: 'Monitor App',
        options: const ['update'],
        value: version,
        note: 'Installs the newest release now, checked against its SHA256SUMS.',
        confirm: 'It restarts its collector; the device is back in a minute or two.',
      );

  /// Starts the update; returns what to tell the asking device.
  static Future<String> start(SyncFolder folder, LocalConfig cfg) async {
    if (Platform.isWindows) {
      final u = await checkForUpdate('windows');
      if (u == null) return 'already on the newest version';
      final dir = Directory.systemTemp.createTempSync('monitor-update');
      final f = await downloadUpdate(u, dir.path);
      folder.log(cfg.id, 'update: installing ${u.version} (asked from another device)');
      await Process.start(f.path, ['/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART'], mode: ProcessStartMode.detached);
      return 'installing ${u.version}';
    }
    final dir = _flagDir;
    if (dir == null) throw StateError('this device has no update service; see packaging/docker/README.md "Update"');
    File('$dir/update-now').writeAsStringSync(DateTime.now().toUtc().toIso8601String(), flush: true);
    return 'asked the update service; it installs a newer version within a minute if there is one';
  }
}
