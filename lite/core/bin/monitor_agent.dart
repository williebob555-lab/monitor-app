import 'dart:io';

import 'package:monitor_core/agent.dart';
import 'package:monitor_core/monitor_core.dart';

/// monitor-agent             run forever (started by systemd or at Windows sign-in)
/// monitor-agent --once      one cycle, then exit (for testing)
/// monitor-agent --folder P  set the synced folder and exit
/// monitor-agent --version
Future<void> main(List<String> args) async {
  if (args.contains('--version')) {
    stdout.writeln(appVersion);
    return;
  }
  final fi = args.indexOf('--folder');
  if (fi >= 0 && fi + 1 < args.length) {
    final c = LocalConfig.load()..folder = args[fi + 1];
    c.save();
    stdout.writeln('Folder set to ${c.folder} (device id ${c.id}).');
    return;
  }
  // One instance per user: hold a lock on a file next to the config.
  Directory(LocalConfig.dir).createSync(recursive: true);
  final lock = File('${LocalConfig.dir}${Platform.pathSeparator}agent.lock').openSync(mode: FileMode.write);
  try {
    lock.lockSync(FileLock.exclusive);
  } catch (_) {
    exit(0); // already running
  }
  final agent = Agent();
  if (args.contains('--once')) {
    agent.host.sample();
    await Future<void>.delayed(const Duration(seconds: 2));
    await agent.cycle(firstRun: true);
    exit(0);
  }
  await agent.run();
}
