import 'dart:convert';
import 'dart:io';

import 'format.dart';
import 'report.dart';
import 'topology.dart';

/// The Syncthing-shared folder. Each device writes only inside its own
/// devices/<id>/ directory, so Syncthing never sees two writers for a file.
class SyncFolder {
  SyncFolder(this.root);
  final String root;

  String get devicesPath => '$root${Platform.pathSeparator}$devicesDirName';
  String deviceDir(String id) => '$devicesPath${Platform.pathSeparator}$id';
  String _p(String id, String name) => '${deviceDir(id)}${Platform.pathSeparator}$name';

  bool get exists => Directory(root).existsSync();

  /// Syncthing puts a .stfolder marker at the root of every folder it shares.
  /// The Monitor folder may be that root or any subfolder of it (for example
  /// Infrastructure/Monitor inside a bigger shared folder).
  bool get sharedBySyncthing => syncthingRoot != null;

  String? get syncthingRoot {
    var d = Directory(root).absolute;
    for (var i = 0; i < 12; i++) {
      if (FileSystemEntity.typeSync('${d.path}${Platform.pathSeparator}.stfolder') != FileSystemEntityType.notFound) return d.path;
      final up = d.parent;
      if (up.path == d.path) break;
      d = up;
    }
    return null;
  }

  void prepare(String id) {
    Directory(deviceDir(id)).createSync(recursive: true);
  }

  /// Write now.json atomically: temp file, then rename over the old one. The
  /// temp name uses Syncthing's own temp pattern (.syncthing.*.tmp), which
  /// Syncthing never syncs, so no ignore file is needed anywhere.
  void writeNow(Report r) {
    final tmp = File(_p(r.id, '.syncthing.$nowFileName.tmp'));
    tmp.writeAsStringSync(jsonEncode(r.toJson()), flush: true);
    tmp.renameSync(_p(r.id, nowFileName));
  }

  /// Append one line to this device's own log; keep the file small.
  void log(String id, String line) {
    try {
      final f = File(_p(id, logFileName));
      // Local time with its UTC offset, so readers can line logs up with
      // history (gaps.dart); older lines have no offset.
      final n = DateTime.now();
      final o = n.timeZoneOffset;
      final stamp = '${n.toIso8601String().substring(0, 19)}${o.isNegative ? '-' : '+'}'
          '${o.inHours.abs().toString().padLeft(2, '0')}:${(o.inMinutes.abs() % 60).toString().padLeft(2, '0')}';
      f.writeAsStringSync('$stamp $line\n', mode: FileMode.append, flush: true);
      if (f.lengthSync() > logMaxBytes) {
        final text = f.readAsStringSync();
        final cut = text.indexOf('\n', text.length ~/ 2);
        f.writeAsStringSync(text.substring(cut + 1));
      }
    } catch (_) {
      // Logging must never break collection.
    }
  }

  final _cache = <String, (String, Report?)>{};

  /// Every device's latest report. A file is parsed again only when its text
  /// changed. The text is compared, not the file time: Syncthing on Android
  /// was seen keeping the first write's time on every later copy of now.json.
  Map<String, Report> readReports() {
    final out = <String, Report>{};
    final dir = Directory(devicesPath);
    if (!dir.existsSync()) return out;
    for (final e in dir.listSync()) {
      if (e is! Directory) continue;
      final f = File('${e.path}${Platform.pathSeparator}$nowFileName');
      try {
        final s = f.statSync();
        if (s.type != FileSystemEntityType.file) continue;
        if (s.size > 1024 * 1024) continue; // not a report
        final text = f.readAsStringSync();
        final c = _cache[f.path];
        Report? r;
        if (c != null && c.$1 == text) {
          r = c.$2;
        } else {
          Report? parsed;
          try {
            parsed = Report.tryParse(jsonDecode(text));
          } catch (_) {}
          // A half-synced file fails to parse: keep showing the previous one.
          if (parsed == null && c?.$2 != null) {
            r = c!.$2;
          } else {
            r = parsed;
            _cache[f.path] = (text, r);
          }
        }
        if (r != null) out[r.id] = r;
      } catch (_) {
        final c = _cache[f.path];
        if (c?.$2 != null) out[c!.$2!.id] = c.$2!;
      }
    }
    return out;
  }

  String thingDir(String host, String id) => '${deviceDir(host)}${Platform.pathSeparator}$thingsDirName${Platform.pathSeparator}$id';

  /// Writes an attached thing's now.json into its host's folder.
  void writeThing(String host, Report r) {
    final d = Directory(thingDir(host, r.id))..createSync(recursive: true);
    final tmp = File('${d.path}${Platform.pathSeparator}.syncthing.$nowFileName.tmp');
    tmp.writeAsStringSync(jsonEncode(r.toJson()), flush: true);
    tmp.renameSync('${d.path}${Platform.pathSeparator}$nowFileName');
  }

  /// Ids of the things this host has written before.
  List<String> thingIds(String host) {
    final d = Directory('${deviceDir(host)}${Platform.pathSeparator}$thingsDirName');
    if (!d.existsSync()) return const [];
    return [for (final e in d.listSync()) if (e is Directory) e.uri.pathSegments.where((x) => x.isNotEmpty).last];
  }

  /// Every attached thing in the folder. A thing can sit under two device
  /// folders (one per system of a dual-boot machine); the newer report wins.
  Map<String, Thing> readThings() {
    final out = <String, Thing>{};
    final dir = Directory(devicesPath);
    if (!dir.existsSync()) return out;
    for (final host in dir.listSync()) {
      if (host is! Directory) continue;
      final hostId = host.uri.pathSegments.where((x) => x.isNotEmpty).last;
      final td = Directory('${host.path}${Platform.pathSeparator}$thingsDirName');
      if (!td.existsSync()) continue;
      for (final t in td.listSync()) {
        if (t is! Directory) continue;
        final r = _readCached(File('${t.path}${Platform.pathSeparator}$nowFileName'));
        if (r == null) continue;
        Map<String, Object?>? params;
        final pf = File('${t.path}${Platform.pathSeparator}$paramsFileName');
        try {
          if (pf.existsSync() && pf.lengthSync() < 1024 * 1024) {
            final j = jsonDecode(pf.readAsStringSync());
            if (j is Map) params = Map<String, Object?>.from(j);
          }
        } catch (_) {}
        final old = out[r.id];
        if (old == null || r.time.isAfter(old.report.time)) out[r.id] = Thing(r, hostId, params: params);
      }
    }
    return out;
  }

  Report? _readCached(File f) {
    try {
      final s = f.statSync();
      if (s.type != FileSystemEntityType.file || s.size > 1024 * 1024) return null;
      final text = f.readAsStringSync();
      final c = _cache[f.path];
      if (c != null && c.$1 == text) return c.$2;
      Report? parsed;
      try {
        parsed = Report.tryParse(jsonDecode(text));
      } catch (_) {}
      if (parsed == null && c?.$2 != null) return c!.$2;
      _cache[f.path] = (text, parsed);
      return parsed;
    } catch (_) {
      return _cache[f.path]?.$2;
    }
  }

  /// Leftovers that Syncthing or interrupted writes can create. All of them
  /// are ignored when reading; this lists them so they can be shown and,
  /// for conflict copies, cleaned up.
  FolderHealth health() {
    final h = FolderHealth();
    final dir = Directory(devicesPath);
    for (final d in dir.existsSync() ? dir.listSync() : const <FileSystemEntity>[]) {
      if (d is! Directory) continue;
      final name = d.uri.pathSegments.where((x) => x.isNotEmpty).last;
      var hasNow = false;
      for (final f in d.listSync()) {
        final n = f.uri.pathSegments.last;
        if (n.contains('.sync-conflict-')) h.conflicts.add(f.path);
        if (n == nowFileName) {
          hasNow = true;
          final c = _cache[f.path];
          if (c != null && c.$2 == null) h.unreadable.add(name);
        }
      }
      if (!hasNow) h.empty.add(name);
    }
    // Shared model items (model.dart): conflict copies of item.json.
    final items = Directory('$root${Platform.pathSeparator}model${Platform.pathSeparator}items');
    if (items.existsSync()) {
      for (final d in items.listSync()) {
        if (d is! Directory) continue;
        for (final f in d.listSync()) {
          if (f is File && f.uri.pathSegments.last.startsWith('item.sync-conflict-')) h.conflicts.add(f.path);
        }
      }
    }
    return h;
  }

  /// Deletes Syncthing conflict copies inside devices/. They are duplicates
  /// that nothing reads; deleting them syncs to the other devices.
  int removeConflicts() {
    var n = 0;
    for (final p in health().conflicts) {
      // Model conflict copies may be the newest version: ModelStore
      // .resolveConflicts() keeps that one and removes the others.
      if (!p.startsWith(devicesPath)) continue;
      try {
        File(p).deleteSync();
        n++;
      } catch (_) {}
    }
    return n;
  }

  String get watchPath => '$root${Platform.pathSeparator}$watchDirName';
  File _watchFile(String id) => File('$watchPath${Platform.pathSeparator}$id.json');

  /// Say "someone is looking at this device's screen right now" until
  /// [watchHoldSeconds] from now. Each device writes only its own file.
  void writeWatch(String id, String name, DateTime now) {
    try {
      Directory(watchPath).createSync(recursive: true);
      final until = now.toUtc().millisecondsSinceEpoch ~/ 1000 + watchHoldSeconds;
      // In place, with the time set explicitly: on Android a renamed file
      // kept an old modification time and Syncthing skipped it.
      final f = _watchFile(id);
      f.writeAsStringSync(jsonEncode({'id': id, 'name': name, 'until': until}), flush: true);
      f.setLastModifiedSync(DateTime.now());
    } catch (_) {}
  }

  /// The app went to the background: stop asking for fast collection.
  void clearWatch(String id) {
    try {
      final f = _watchFile(id);
      if (f.existsSync()) f.deleteSync();
    } catch (_) {}
  }

  /// True while this device's own app is open (its watch file is fresh).
  bool watchedHere(String id, DateTime now) {
    try {
      final j = jsonDecode(_watchFile(id).readAsStringSync());
      return j is Map && j['until'] is num && (j['until'] as num) > now.toUtc().millisecondsSinceEpoch ~/ 1000;
    } catch (_) {
      return false;
    }
  }

  /// Names of the devices whose app is open right now (fresh watch files).
  /// Costs one small directory listing.
  List<String> watchers(DateTime now) {
    final out = <String>[];
    final d = Directory(watchPath);
    if (!d.existsSync()) return out;
    final t = now.toUtc().millisecondsSinceEpoch ~/ 1000;
    for (final e in d.listSync()) {
      if (e is! File || !e.path.endsWith('.json') || e.path.contains('.syncthing.')) continue;
      try {
        final j = jsonDecode(e.readAsStringSync());
        if (j is Map && j['until'] is num && (j['until'] as num) > t) out.add('${j['name'] ?? j['id'] ?? '?'}');
      } catch (_) {}
    }
    return out;
  }

  /// The last [maxBytes] of a device's log.
  String readLog(String id, {int maxBytes = 64 * 1024}) {
    final f = File(_p(id, logFileName));
    if (!f.existsSync()) return '';
    final raf = f.openSync();
    try {
      final len = raf.lengthSync();
      final start = len > maxBytes ? len - maxBytes : 0;
      raf.setPositionSync(start);
      return utf8.decode(raf.readSync(len - start), allowMalformed: true);
    } finally {
      raf.closeSync();
    }
  }
}

class FolderHealth {
  final conflicts = <String>[];

  /// Device folders whose now.json could not be read (half-synced or damaged).
  final unreadable = <String>[];

  /// Device folders with no now.json at all.
  final empty = <String>[];

  /// Declarations that do not agree: a parent or link naming a missing
  /// file, a child its parent does not list (see checkTopology).
  final topology = <String>[];

  bool get clean => conflicts.isEmpty && unreadable.isEmpty && empty.isEmpty && topology.isEmpty;
}
