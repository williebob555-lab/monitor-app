import 'dart:io';

import '../config.dart';
import '../control.dart';
import '../folder.dart';
import '../update.dart';

/// "Update now", asked from any device (control "update").
///
/// Windows: the collector downloads the newest release (checked against its
/// SHA256SUMS) and runs the per-user installer silently, which restarts it.
/// Linux servers and packages are updated on the machine itself (the nightly
/// Docker updater, or the app's Install there), so they don't offer it.
/// Android can't install without a tap on the phone; its collector posts a
/// notification instead (Collector.kt).
class Updater {
  static bool get canUpdate => Platform.isWindows;

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

  /// Linux package: the running binary was replaced by an update (dnf/apt,
  /// or the app's Install), so systemd should start the new one.
  static bool get replaced {
    if (!Platform.isLinux || File('/.dockerenv').existsSync()) return false;
    try {
      return Link('/proc/self/exe').targetSync().endsWith(' (deleted)');
    } catch (_) {
      return false;
    }
  }

  /// Starts the update; returns what to tell the asking device.
  static Future<String> start(SyncFolder folder, LocalConfig cfg) async {
    if (!Platform.isWindows) throw StateError('update this device on the machine itself');
    final u = await checkForUpdate('windows');
    if (u == null) return 'already on the newest version';
    final dir = Directory.systemTemp.createTempSync('monitor-update');
    final f = await downloadUpdate(u, dir.path);
    folder.log(cfg.id, 'update: installing ${u.version} (asked from another device)');
    await Process.start(f.path, ['/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART'], mode: ProcessStartMode.detached);
    return 'installing ${u.version}';
  }
}
