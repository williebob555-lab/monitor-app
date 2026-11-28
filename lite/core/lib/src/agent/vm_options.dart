import 'dart:ffi';
import 'dart:io';

import 'package:ffi/ffi.dart';

/// Memory budget (roadmap row 2). The Dart VM lets its young-object space
/// grow to 32 MB per half before it stops growing, so a collector that keeps
/// only a few MB alive still settles at about 60 MB (wc-beta and the laptop,
/// 2026-11-28). Capping that space at 2 MB and handing freed pages back to
/// the system kept the same work at about 12 MB in a test, for about 6 %
/// more CPU.
///
/// The VM reads these flags only at start, from `DART_VM_OPTIONS` (comma
/// separated), so the collector restarts itself once with them: on Linux
/// it replaces itself in place (same process, so systemd and Docker see no
/// change), on Windows it starts a copy and exits. `MONITOR_VM_OPTIONS=off`
/// turns this off; anything already in `DART_VM_OPTIONS` is kept.
const vmOptions = '--new_gen_semi_max_size=2,--dontneed_on_sweep';

/// The `DART_VM_OPTIONS` to restart with, or null when nothing to do.
String? vmOptionsToAdd(Map<String, String> env, String executable) {
  if (env['MONITOR_VM_OPTIONS'] == 'off') return null;
  final have = env['DART_VM_OPTIONS'] ?? '';
  if (have.contains('new_gen_semi_max_size')) return null;
  // `dart run` (development): the flags would go to the dart tool.
  final exe = executable.toLowerCase().split(RegExp(r'[\\/]')).last;
  if (exe == 'dart' || exe == 'dart.exe') return null;
  return have.isEmpty ? vmOptions : '$have,$vmOptions';
}

/// Restarts this process with [vmOptions] when they are not set yet.
/// Returns normally (and the caller carries on) when that is not needed or
/// not possible.
Future<void> ensureVmOptions(List<String> args) async {
  final exe = Platform.resolvedExecutable;
  final add = vmOptionsToAdd(Platform.environment, exe);
  if (add == null) return;
  try {
    if (Platform.isLinux) {
      final libc = DynamicLibrary.process();
      final setenv = libc.lookupFunction<Int32 Function(Pointer<Utf8>, Pointer<Utf8>, Int32), int Function(Pointer<Utf8>, Pointer<Utf8>, int)>('setenv');
      final execv = libc.lookupFunction<Int32 Function(Pointer<Utf8>, Pointer<Pointer<Utf8>>), int Function(Pointer<Utf8>, Pointer<Pointer<Utf8>>)>('execv');
      setenv('DART_VM_OPTIONS'.toNativeUtf8(), add.toNativeUtf8(), 1);
      final argv = calloc<Pointer<Utf8>>(args.length + 2);
      argv[0] = exe.toNativeUtf8();
      for (var i = 0; i < args.length; i++) {
        argv[i + 1] = args[i].toNativeUtf8();
      }
      argv[args.length + 1] = nullptr;
      execv('/proc/self/exe'.toNativeUtf8(), argv); // returns only on failure
    } else if (Platform.isWindows) {
      await Process.start(exe, args, environment: {'DART_VM_OPTIONS': add}, mode: ProcessStartMode.detached);
      exit(0);
    }
  } catch (_) {
    // Carry on without the flags rather than not collecting at all.
  }
}
