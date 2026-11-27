import 'dart:convert';
import 'dart:io';
import 'dart:math';

import 'folder.dart';

/// A setting a device offers to change for anyone in the shared folder, as
/// declared in its own now.json (or one of its things'): "controls".
///
/// Options are the values to pick from; an action (like "update") has one
/// option. The device applies a change only for a control it declares.
class Control {
  Control({required this.key, required this.label, required this.options, this.value, this.note, this.confirm});
  final String key, label;
  final List<String> options;

  /// What it is set to now, if it has a state.
  final String? value;
  final String? note;

  /// What the app says before sending it ("The UPS stops beeping...").
  final String? confirm;

  static Control? tryParse(Object? j) {
    if (j is! Map || j['key'] is! String || j['options'] is! List) return null;
    return Control(
      key: j['key'] as String,
      label: '${j['label'] ?? j['key']}',
      options: [for (final o in j['options'] as List) '$o'],
      value: j['value'] is String ? j['value'] as String : null,
      note: j['note'] is String ? j['note'] as String : null,
      confirm: j['confirm'] is String ? j['confirm'] as String : null,
    );
  }

  Map<String, Object?> toJson() => {
        'key': key,
        'label': label,
        'options': options,
        if (value != null) 'value': value,
        if (note != null) 'note': note,
        if (confirm != null) 'confirm': confirm,
      };
}

/// "Please set [key] of [target] to [value]", written by the asking device
/// into its own folder: devices/<from>/requests/<id>.json.
class ControlRequest {
  ControlRequest({required this.id, required this.from, required this.fromName, required this.host, required this.target, required this.key, required this.value, required this.time});
  final String id, from, fromName;

  /// The device that applies it, and the device or thing it is for.
  final String host, target;
  final String key, value;
  final DateTime time;

  /// Requests older than this are ignored, so an old one can't surprise you.
  static const maxAge = Duration(minutes: 10);

  static ControlRequest? tryParse(Object? j) {
    if (j is! Map) return null;
    final s = [for (final k in const ['id', 'from', 'host', 'target', 'key', 'value']) j[k]];
    if (s.any((x) => x is! String) || j['t'] is! num) return null;
    return ControlRequest(
      id: s[0] as String,
      from: s[1] as String,
      fromName: '${j['fromName'] ?? s[1]}',
      host: s[2] as String,
      target: s[3] as String,
      key: s[4] as String,
      value: s[5] as String,
      time: DateTime.fromMillisecondsSinceEpoch((j['t'] as num).toInt() * 1000, isUtc: true),
    );
  }

  Map<String, Object?> toJson() => {
        'id': id,
        'from': from,
        'fromName': fromName,
        'host': host,
        'target': target,
        'key': key,
        'value': value,
        't': time.millisecondsSinceEpoch ~/ 1000,
      };

  static String newId() {
    final r = Random.secure();
    return List.generate(12, (_) => r.nextInt(256).toRadixString(16).padLeft(2, '0')).join();
  }
}

/// What the applying device did: devices/<host>/results/<id>.json.
class ControlResult {
  ControlResult({required this.id, required this.ok, required this.message, required this.time, this.value});
  final String id;
  final bool ok;
  final String message;
  final String? value;
  final DateTime time;

  static ControlResult? tryParse(Object? j) {
    if (j is! Map || j['id'] is! String || j['ok'] is! bool || j['t'] is! num) return null;
    return ControlResult(
      id: j['id'] as String,
      ok: j['ok'] as bool,
      message: '${j['message'] ?? ''}',
      value: j['value'] is String ? j['value'] as String : null,
      time: DateTime.fromMillisecondsSinceEpoch((j['t'] as num).toInt() * 1000, isUtc: true),
    );
  }

  Map<String, Object?> toJson() => {
        'id': id,
        'ok': ok,
        'message': message,
        if (value != null) 'value': value,
        't': time.millisecondsSinceEpoch ~/ 1000,
      };
}

/// Requests and results in the shared folder. Each device still writes only
/// inside its own devices/<id>/ directory.
extension Controls on SyncFolder {
  String _dir(String id, String sub) => '${deviceDir(id)}${Platform.pathSeparator}$sub';

  void _write(String dir, String name, Map<String, Object?> j) {
    Directory(dir).createSync(recursive: true);
    final f = File('$dir${Platform.pathSeparator}$name');
    final tmp = File('${f.path}.tmp');
    tmp.writeAsStringSync(jsonEncode(j), flush: true);
    tmp.renameSync(f.path);
  }

  List<Map<String, Object?>> _read(String dir) {
    final d = Directory(dir);
    if (!d.existsSync()) return const [];
    final out = <Map<String, Object?>>[];
    for (final e in d.listSync()) {
      if (e is! File || !e.path.endsWith('.json') || e.path.contains('.sync-conflict')) continue;
      try {
        final j = jsonDecode(e.readAsStringSync());
        if (j is Map<String, Object?>) out.add(j);
      } catch (_) {}
    }
    return out;
  }

  void sendRequest(ControlRequest r) => _write(_dir(r.from, 'requests'), '${r.id}.json', r.toJson());

  /// Requests for [host] from every device, newest first, still in time.
  List<ControlRequest> requestsFor(String host, DateTime now) {
    final out = <ControlRequest>[];
    final d = Directory(devicesPath);
    if (!d.existsSync()) return out;
    for (final dev in d.listSync().whereType<Directory>()) {
      for (final j in _read('${dev.path}${Platform.pathSeparator}requests')) {
        final r = ControlRequest.tryParse(j);
        if (r == null || r.host != host) continue;
        if (now.difference(r.time).abs() > ControlRequest.maxAge) continue;
        out.add(r);
      }
    }
    out.sort((a, b) => b.time.compareTo(a.time));
    return out;
  }

  void writeResult(String host, ControlResult r) => _write(_dir(host, 'results'), '${r.id}.json', r.toJson());

  ControlResult? resultOf(String host, String id) {
    final f = File('${_dir(host, 'results')}${Platform.pathSeparator}$id.json');
    try {
      return f.existsSync() ? ControlResult.tryParse(jsonDecode(f.readAsStringSync())) : null;
    } catch (_) {
      return null;
    }
  }

  bool handled(String host, String id) => File('${_dir(host, 'results')}${Platform.pathSeparator}$id.json').existsSync();

  /// This device's own requests, for showing what is still waiting.
  List<ControlRequest> myRequests(String me) => _read(_dir(me, 'requests')).map(ControlRequest.tryParse).whereType<ControlRequest>().toList();

  /// Keeps both folders small: requests and results older than a day go.
  void pruneControls(String me, DateTime now) {
    for (final sub in const ['requests', 'results']) {
      final d = Directory(_dir(me, sub));
      if (!d.existsSync()) continue;
      for (final e in d.listSync().whereType<File>()) {
        try {
          if (now.difference(e.lastModifiedSync().toUtc()) > const Duration(days: 1)) e.deleteSync();
        } catch (_) {}
      }
    }
  }
}
