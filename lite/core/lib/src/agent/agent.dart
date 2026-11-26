import 'dart:async';
import 'dart:io';

import '../config.dart';
import '../folder.dart';
import '../history.dart';
import '../report.dart';
import '../status.dart' show freshLimit;
import '../version.dart';
import 'linux.dart';
import 'sample.dart';
import 'windows.dart';

/// Budgets the background program holds itself to. Breaking one is written
/// to its own log and into now.json so it shows in the app.
const budgetRssMb = 40.0;
const budgetCycleCpuMs = 300;
const budgetCycleMs = 8000;

/// Syncthing's sync port. A TCP connect over Tailscale tells three things
/// apart: open (device up, Syncthing running), refused (device up, Syncthing
/// not running), timeout (no route to the device).
const syncthingPort = 22000;

bool isTailscaleIp(InternetAddress a) {
  if (a.type != InternetAddressType.IPv4) return false;
  final b = a.rawAddress;
  return b[0] == 100 && b[1] >= 64 && b[1] <= 127;
}

Future<String?> tailscaleIp() async {
  try {
    for (final i in await NetworkInterface.list(type: InternetAddressType.IPv4)) {
      for (final a in i.addresses) {
        if (isTailscaleIp(a)) return a.address;
      }
    }
  } catch (_) {}
  return null;
}

Future<String> tcpProbe(String host, {Duration timeout = const Duration(seconds: 3)}) async {
  try {
    final s = await Socket.connect(host, syncthingPort, timeout: timeout);
    s.destroy();
    return 'open';
  } on SocketException catch (e) {
    final code = e.osError?.errorCode;
    // ECONNREFUSED: Linux 111, Windows 10061, macOS 61.
    if (code == 111 || code == 10061 || code == 61) return 'refused';
    if (e.message.contains('timed out') || code == 110 || code == 10060) return 'timeout';
    return 'timeout';
  } catch (_) {
    return 'error';
  }
}

class Agent {
  Agent({Host? host}) : host = host ?? (Platform.isWindows ? WindowsHost() : LinuxHost());
  final Host host;
  final interval = const Duration(seconds: 60);

  int _seq = 0;
  String? _machine;
  String? _os;
  String? _lastFolder;
  DateTime? _lastPrune;
  DateTime? _lastSummary;
  int _cycles = 0, _maxCycleMs = 0;
  double _maxRss = 0;

  /// Runs forever, one cycle a minute. Timer-driven: the process sleeps in
  /// between and does nothing else.
  Future<void> run() async {
    host.sample(); // prime the counters so the first rates are real
    await Future<void>.delayed(const Duration(seconds: 5));
    await cycle(firstRun: true);
    Timer.periodic(interval, (_) => cycle());
    await Completer<void>().future;
  }

  bool _busy = false;

  Future<void> cycle({bool firstRun = false}) async {
    if (_busy) return; // never overlap cycles
    _busy = true;
    final sw = Stopwatch()..start();
    final cpu0 = host.processCpuMs();
    LocalConfig? cfg;
    SyncFolder? folder;
    try {
      if (_os == null) {
        _os = host.osName();
        _machine = host.machineKey();
      }
      cfg = LocalConfig.load();
      folder = SyncFolder(cfg.folder);
      folder.prepare(cfg.id);
      if (firstRun || _lastFolder != cfg.folder) {
        folder.log(cfg.id, 'start agent $appVersion on $_os, folder ${cfg.folder}, pid $pid');
        _lastFolder = cfg.folder;
      }
      final s = host.sample();
      final ip = await tailscaleIp();
      final stLocal = await tcpProbe('127.0.0.1', timeout: const Duration(seconds: 1));
      final others = folder.readReports()..remove(cfg.id);
      final now = DateTime.now().toUtc();
      final peers = <String, PeerView>{};
      await Future.wait(others.values.where((o) => _machine == null || o.machine != _machine).map((o) async {
        final seen = now.difference(o.time).inSeconds;
        if (o.tailscaleIp == null || ip == null) {
          peers[o.id] = PeerView(tcp: 'noip', seenAge: seen);
          return;
        }
        // Skip devices silent for over a day: probing them is wasted effort.
        if (seen > 86400) return;
        final tcp = await tcpProbe(o.tailscaleIp!);
        final ms = await host.ping(o.tailscaleIp!);
        peers[o.id] = PeerView(tcp: tcp, pingMs: ms, seenAge: seen);
      }));

      final rss = ProcessInfo.currentRss / (1024 * 1024);
      final cpu1 = host.processCpuMs();
      final cycleCpu = cpu0 != null && cpu1 != null ? cpu1 - cpu0 : null;
      final over = <String>[
        if (rss > budgetRssMb) 'memory ${rss.toStringAsFixed(1)} MB > ${budgetRssMb.round()} MB',
        if (cycleCpu != null && cycleCpu > budgetCycleCpuMs) 'cpu ${cycleCpu} ms per cycle > $budgetCycleCpuMs ms',
        if (sw.elapsedMilliseconds > budgetCycleMs) 'cycle ${sw.elapsedMilliseconds} ms > $budgetCycleMs ms',
      ];
      final r = Report(
        id: cfg.id,
        name: cfg.name ?? Platform.localHostname,
        kind: host.kind,
        time: now,
        os: _os,
        machine: _machine,
        app: appVersion,
        seq: ++_seq,
        nextSeconds: interval.inSeconds,
        bootTime: s.bootTime,
        battery: s.battery,
        cpuPct: s.cpuPct,
        memUsed: s.memUsed,
        memTotal: s.memTotal,
        diskUsed: s.diskUsed,
        diskTotal: s.diskTotal,
        netRxBps: s.netRxBps,
        netTxBps: s.netTxBps,
        netType: s.netType,
        tailscaleIp: ip,
        syncthingLocal: stLocal == 'open',
        peers: peers,
        self: SelfCost(rssMb: rss, runMs: sw.elapsedMilliseconds, cpuMs: cycleCpu, over: over),
      );
      folder.writeNow(r);
      final hw = HistoryWriter(folder.deviceDir(cfg.id), LocalConfig.dir);
      hw.add(r.historyLine(), now);
      if (over.isNotEmpty) folder.log(cfg.id, 'OVER BUDGET: ${over.join('; ')}');
      _cycles++;
      _maxCycleMs = sw.elapsedMilliseconds > _maxCycleMs ? sw.elapsedMilliseconds : _maxCycleMs;
      _maxRss = rss > _maxRss ? rss : _maxRss;
      if (_lastSummary == null || now.difference(_lastSummary!) > const Duration(hours: 1)) {
        final seen = others.values
            .map((o) => '${o.name}:${peers[o.id]?.tcp ?? '-'}/${peers[o.id]?.pingMs ?? '-'}ms/'
                '${now.difference(o.time).inSeconds > freshLimit(o).inSeconds ? 'stale' : 'fresh'}')
            .join(' ');
        folder.log(cfg.id,
            'ok cycles=$_cycles maxCycle=${_maxCycleMs}ms maxRss=${_maxRss.toStringAsFixed(1)}MB ts=${ip ?? 'none'} st=$stLocal peers[$seen]');
        _lastSummary = now;
        _cycles = 0;
        _maxCycleMs = 0;
        _maxRss = 0;
      }
      if (_lastPrune == null || now.difference(_lastPrune!) > const Duration(hours: 6)) {
        final used = hw.prune(now, Retention.load(cfg.folder));
        folder.log(cfg.id, 'history ${(used / 1024).round()} KB');
        _lastPrune = now;
      }
    } catch (e, st) {
      if (cfg != null && folder != null) {
        folder.log(cfg.id, 'ERROR cycle failed: $e ${st.toString().split('\n').take(3).join(' | ')}');
      } else {
        stderr.writeln('cycle failed: $e');
      }
    } finally {
      _busy = false;
    }
  }
}
