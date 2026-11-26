import 'dart:async';
import 'dart:convert';
import 'dart:io';

import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';

import 'host.dart';

/// Budget for the app itself while it is open.
const budgetAppRssMb = 150.0;

/// Reads the shared folder while the app is visible. Nothing runs while the
/// app is in the background or the screen is off: the timer is cancelled.
class AppState extends ChangeNotifier with WidgetsBindingObserver {
  AppState(this.host) : folder = SyncFolder(host.folder);

  final LocalHost host;
  SyncFolder folder;
  /// Reports shown (hidden ones removed).
  Map<String, Report> reports = {};

  /// Devices hidden on this device: id -> time hidden. Local only, never
  /// synced. A hidden device comes back by itself if it reports again.
  Map<String, DateTime> hidden = {};
  List<Report> hiddenReports = [];
  FolderHealth health = FolderHealth();
  Assessment assessment = assess(const {});
  Timer? _timer;
  String _sig = '';
  DateTime _lastNotify = DateTime(2000);
  DateTime? _lastBudgetLog;

  UpdateInfo? update;
  String? updateMessage;
  bool checking = false;
  double? downloadProgress;
  DateTime? _lastUpdateCheck;

  File get _hiddenFile => File('${host.stateDir}${Platform.pathSeparator}hidden.json');

  void _loadHidden() {
    try {
      final j = jsonDecode(_hiddenFile.readAsStringSync()) as Map<String, Object?>;
      hidden = {
        for (final e in j.entries)
          if (e.value is num) e.key: DateTime.fromMillisecondsSinceEpoch((e.value! as num).toInt() * 1000, isUtc: true),
      };
    } catch (_) {}
  }

  void _saveHidden() {
    try {
      Directory(host.stateDir).createSync(recursive: true);
      _hiddenFile.writeAsStringSync(jsonEncode({for (final e in hidden.entries) e.key: e.value.millisecondsSinceEpoch ~/ 1000}));
    } catch (e) {
      host.log('could not save hidden list: $e');
    }
  }

  void hide(String id) {
    hidden[id] = DateTime.now().toUtc();
    _saveHidden();
    host.log('hid device $id');
    refresh(force: true);
  }

  void unhide(String id) {
    hidden.remove(id);
    _saveHidden();
    refresh(force: true);
  }

  int removeConflicts() {
    final n = folder.removeConflicts();
    host.log('removed $n Syncthing conflict copies');
    refresh(force: true);
    return n;
  }

  void start() {
    _loadHidden();
    WidgetsBinding.instance.addObserver(this);
    host.log('open ${host.kind} app ${host.version}, folder ${host.folder}');
    host.nudge().then((_) => refresh(force: true));
    _resume();
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    _timer?.cancel();
    super.dispose();
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    final s = state;
    if (s == AppLifecycleState.resumed) {
      host.nudge().then((_) => refresh(force: true));
      _resume();
    } else if (s == AppLifecycleState.paused || s == AppLifecycleState.hidden) {
      _timer?.cancel();
      _timer = null;
    }
  }

  void _resume() {
    _timer?.cancel();
    refresh(force: true);
    _timer = Timer.periodic(const Duration(seconds: 10), (_) => refresh());
    if (_lastUpdateCheck == null || DateTime.now().difference(_lastUpdateCheck!) > const Duration(hours: 6)) {
      checkUpdate(quiet: true);
    }
  }

  Future<void> setFolder(String path) async {
    await host.setFolder(path);
    folder = SyncFolder(path);
    await host.nudge();
    refresh(force: true);
  }

  void refresh({bool force = false}) {
    Map<String, Report> all;
    try {
      all = folder.readReports();
    } catch (_) {
      all = {};
    }
    // Hidden stays hidden only while the device stays silent.
    final back = hidden.entries.where((e) => all[e.key] != null && all[e.key]!.time.isAfter(e.value)).map((e) => e.key).toList();
    if (back.isNotEmpty) {
      back.forEach(hidden.remove);
      _saveHidden();
    }
    reports = {for (final e in all.entries) if (!hidden.containsKey(e.key)) e.key: e.value};
    hiddenReports = all.values.where((r) => hidden.containsKey(r.id)).toList()..sort((a, b) => a.name.compareTo(b.name));
    final now = DateTime.now().toUtc();
    final sig = reports.values.map((r) => '${r.id}:${r.seq}:${r.time.millisecondsSinceEpoch}').join(',');
    // Redraw when data changed, or every 30 s so "x min ago" stays true.
    if (!force && sig == _sig && now.difference(_lastNotify) < const Duration(seconds: 30)) return;
    _sig = sig;
    _lastNotify = now;
    assessment = assess(reports, myId: host.id, now: now);
    try {
      health = folder.health();
    } catch (_) {}
    notifyListeners();
    _checkBudget();
  }

  void _checkBudget() {
    final now = DateTime.now();
    if (_lastBudgetLog != null && now.difference(_lastBudgetLog!) < const Duration(minutes: 30)) return;
    _lastBudgetLog = now;
    final rss = ProcessInfo.currentRss / (1024 * 1024);
    final line = 'app memory ${rss.toStringAsFixed(1)} MB, ${reports.length} devices';
    host.log(rss > budgetAppRssMb ? 'OVER BUDGET: $line > ${budgetAppRssMb.round()} MB' : line);
  }

  Future<void> checkUpdate({bool quiet = false}) async {
    if (checking) return;
    checking = true;
    if (!quiet) {
      updateMessage = 'Checking...';
      notifyListeners();
    }
    try {
      update = await checkForUpdate(host.kind, linuxPackage: host.kind == 'linux' ? host.linuxPackage : 'rpm', current: host.version);
      updateMessage = update == null ? 'You have the newest version (${host.version}).' : null;
      _lastUpdateCheck = DateTime.now();
    } catch (e) {
      updateMessage = quiet ? null : 'Could not check for updates: $e';
    } finally {
      checking = false;
      notifyListeners();
    }
  }

  Future<void> installUpdate() async {
    final u = update;
    if (u == null) return;
    try {
      downloadProgress = 0;
      notifyListeners();
      final f = await downloadUpdate(u, host.tempDir, progress: (p) {
        downloadProgress = p;
        notifyListeners();
      });
      downloadProgress = null;
      updateMessage = 'Downloaded and checked. Opening the installer...';
      notifyListeners();
      await host.install(f);
    } catch (e) {
      downloadProgress = null;
      updateMessage = 'Update failed: $e';
      host.log('update failed: $e');
      notifyListeners();
    }
  }
}
