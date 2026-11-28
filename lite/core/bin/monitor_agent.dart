import 'dart:io';

import 'package:monitor_core/agent.dart';
import 'package:monitor_core/monitor_core.dart';

/// monitor-agent             run forever (started by systemd or at Windows sign-in)
/// monitor-agent --once      one cycle, then exit (for testing)
/// monitor-agent --folder P  set the synced folder and exit
/// monitor-agent --version
/// monitor-agent --ups-alarm off|on|quiet   set the USB UPS's beeper (Linux)
Future<void> main(List<String> args) async {
  final ai = args.indexOf('--ups-alarm');
  if (ai >= 0) {
    final v = {'off': 1, 'on': 2, 'quiet': 3}[ai + 1 < args.length ? args[ai + 1] : ''];
    if (v == null || !Platform.isLinux) {
      stderr.writeln('usage (Linux): monitor-agent --ups-alarm off|on|quiet');
      exit(2);
    }
    final now = HidUps.setAlarm(v, (why) => stderr.writeln(why));
    if (now == null) exit(1);
    stdout.writeln('The UPS alarm is now ${{1: 'off', 2: 'on', 3: 'quiet until the next event'}[now] ?? 'set to $now'}.');
    return;
  }
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
  // Small young-object space (row 2): restarts once with the VM flags.
  await ensureVmOptions(args);
  // One instance per user: hold a lock on a file next to the config.
  Directory(LocalConfig.dir).createSync(recursive: true);
  final lock = File('${LocalConfig.dir}${Platform.pathSeparator}agent.lock').openSync(mode: FileMode.write);
  try {
    lock.lockSync(FileLock.exclusive);
  } catch (_) {
    exit(0); // already running
  }
  final once = args.contains('--once');
  final claim = once ? null : (InstanceClaim('${LocalConfig.dir}${Platform.pathSeparator}agent.owner', pid: pid)..take());
  final agent = Agent(claim: claim);
  if (once) {
    agent.host.sample();
    await Future<void>.delayed(const Duration(seconds: 2));
    await agent.cycle(firstRun: true);
    exit(0);
  }
  await agent.run();
}
