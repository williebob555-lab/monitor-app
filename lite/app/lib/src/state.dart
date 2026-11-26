import 'dart:async';
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
  Map<String, Report> reports = {};
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

  void start() {
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
  void didChangeAppLifecycleState(AppLifecycleState s) {
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
    try {
      reports = folder.readReports();
    } catch (_) {
      reports = {};
    }
    final now = DateTime.now().toUtc();
    final sig = reports.values.map((r) => '${r.id}:${r.seq}:${r.time.millisecondsSinceEpoch}').join(',');
    // Redraw when data changed, or every 30 s so "x min ago" stays true.
    if (!force && sig == _sig && now.difference(_lastNotify) < const Duration(seconds: 30)) return;
    _sig = sig;
    _lastNotify = now;
    assessment = assess(reports, myId: host.id, now: now);
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
