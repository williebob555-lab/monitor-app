import 'dart:ffi';
import 'dart:io';
import 'dart:isolate';

import 'package:ffi/ffi.dart';

import '../config.dart';

/// Shows a desktop notification (roadmap row 20). Returns null when shown,
/// else why not, in words. No-op on servers without a desktop session.
///
/// * Linux: the session's notification service (notify-send, else gdbus).
///   The collector is a user service, so it has the session bus; a Docker
///   collector has none and shows nothing.
/// * Windows: a notification-area balloon, which Windows 10 and 11 show as
///   a toast. Raw Win32 on a short side isolate; no child process, so no
///   window flashes. A marker file switches it off for good if it ever takes
///   the collector down, as the USB listing does.
Future<String?> desktopNotify(String title, String text) async {
  if (Platform.isLinux) return _linux(title, text);
  if (Platform.isWindows) return _windows(title, text);
  return 'not supported here';
}

Future<String?> _linux(String title, String text) async {
  final env = Platform.environment;
  final hasBus = (env['DBUS_SESSION_BUS_ADDRESS'] ?? '').isNotEmpty || (env['XDG_RUNTIME_DIR'] != null && File('${env['XDG_RUNTIME_DIR']}/bus').existsSync());
  if (!hasBus) return 'no desktop session';
  try {
    final r = await Process.run('notify-send', ['-a', 'Monitor App', '-i', 'dialog-information', title, text]).timeout(const Duration(seconds: 5));
    if (r.exitCode == 0) return null;
  } catch (_) {}
  String q(String s) => "'${s.replaceAll(r'\', r'\\').replaceAll("'", r"\'")}'";
  try {
    final r = await Process.run('gdbus', [
      'call', '--session', '--dest', 'org.freedesktop.Notifications', '--object-path', '/org/freedesktop/Notifications', //
      '--method', 'org.freedesktop.Notifications.Notify', q('Monitor App'), '0', q('dialog-information'), q(title), q(text), '[]', '{}', '-1',
    ]).timeout(const Duration(seconds: 5));
    return r.exitCode == 0 ? null : 'gdbus: ${'${r.stderr}'.trim()}';
  } catch (e) {
    return 'no notification tool: $e';
  }
}

bool? _winOk;

Future<String?> _windows(String title, String text) async {
  final marker = File('${LocalConfig.dir}${Platform.pathSeparator}notify.running');
  _winOk ??= !marker.existsSync();
  if (_winOk == false) return 'off: it stopped the collector once (delete notify.running in the Monitor settings folder to try again)';
  try {
    marker.writeAsStringSync('${DateTime.now().toUtc()}', flush: true);
    final ok = await Isolate.run(() => _balloon(title, text));
    marker.deleteSync();
    return ok ? null : 'Windows refused the notification';
  } catch (e) {
    try {
      marker.deleteSync();
    } catch (_) {}
    return '$e';
  }
}

// NOTIFYICONDATAW on 64-bit Windows: 976 bytes.
const _nidSize = 976, _offHwnd = 8, _offId = 16, _offFlags = 20, _offIcon = 32, _offTip = 40, _offInfo = 304, _offTimeout = 816, _offTitle = 820, _offInfoFlags = 948;

bool _balloon(String title, String text) {
  final user32 = DynamicLibrary.open('user32.dll');
  final shell32 = DynamicLibrary.open('shell32.dll');
  final k32 = DynamicLibrary.open('kernel32.dll');
  final createWindow = user32.lookupFunction<
      IntPtr Function(Uint32, Pointer<Utf16>, Pointer<Utf16>, Uint32, Int32, Int32, Int32, Int32, IntPtr, IntPtr, IntPtr, Pointer<Void>),
      int Function(int, Pointer<Utf16>, Pointer<Utf16>, int, int, int, int, int, int, int, int, Pointer<Void>)>('CreateWindowExW');
  final destroyWindow = user32.lookupFunction<Int32 Function(IntPtr), int Function(int)>('DestroyWindow');
  final loadIcon = user32.lookupFunction<IntPtr Function(IntPtr, IntPtr), int Function(int, int)>('LoadIconW');
  final notifyIcon = shell32.lookupFunction<Int32 Function(Uint32, Pointer<Uint8>), int Function(int, Pointer<Uint8>)>('Shell_NotifyIconW');
  final sleep = k32.lookupFunction<Void Function(Uint32), void Function(int)>('Sleep');

  final cls = 'STATIC'.toNativeUtf16(), name = 'Monitor App'.toNativeUtf16();
  final nid = calloc<Uint8>(_nidSize);
  var hwnd = 0;
  try {
    hwnd = createWindow(0, cls, name, 0, 0, 0, 0, 0, 0, 0, 0, nullptr);
    if (hwnd == 0) return false;
    void put(int off, String s, int maxChars) {
      final units = s.codeUnits.take(maxChars - 1).toList();
      final p = (nid + off).cast<Uint16>();
      for (var i = 0; i < units.length; i++) {
        p[i] = units[i];
      }
      p[units.length] = 0;
    }

    nid.cast<Uint32>()[0] = _nidSize;
    (nid + _offHwnd).cast<IntPtr>().value = hwnd;
    (nid + _offId).cast<Uint32>().value = 1;
    (nid + _offFlags).cast<Uint32>().value = 0x02 | 0x04 | 0x10; // NIF_ICON | NIF_TIP | NIF_INFO
    (nid + _offIcon).cast<IntPtr>().value = loadIcon(0, 32516); // IDI_INFORMATION
    put(_offTip, 'Monitor App', 128);
    put(_offInfo, text, 256);
    (nid + _offTimeout).cast<Uint32>().value = 10000;
    put(_offTitle, title, 64);
    (nid + _offInfoFlags).cast<Uint32>().value = 0x01; // NIIF_INFO
    final ok = notifyIcon(0, nid) != 0; // NIM_ADD
    // Long enough for the toast to show; Windows keeps it in the
    // notification centre after the icon goes.
    if (ok) sleep(8000);
    notifyIcon(2, nid); // NIM_DELETE
    return ok;
  } finally {
    if (hwnd != 0) destroyWindow(hwnd);
    calloc.free(nid);
    calloc.free(cls);
    calloc.free(name);
  }
}
