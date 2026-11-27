import 'dart:async';
import 'dart:convert';
import 'dart:io';

import '../config.dart';
import '../folder.dart';
import '../format.dart';
import '../history.dart';
import '../report.dart';
import '../status.dart' show freshLimit;
import '../topology.dart';
import '../version.dart';
import 'extras.dart';
import 'linux.dart';
import 'sample.dart';
import 'things.dart';
import 'containers.dart';
import 'windows.dart';
import 'updater.dart';
import 'hid_ups.dart';
import '../control.dart';

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

  static const fast = Duration(seconds: watchIntervalSeconds);
  DateTime? _lastCycle;
  DateTime? _lastHistory;
  List<String> _watchers = const [];
  final _extras = Extras();
  final _things = Things();
  final _containers = Containers();
  DateTime? _lastThingHistory;

  /// Runs forever. Resting rate is one cycle a minute. While someone has the
  /// app open on any device (a fresh file in watch/), one every 10 s. The
  /// process wakes every 10 s only to list that small folder.
  Future<void> run() async {
    host.sample(); // prime the counters so the first rates are real
    await Future<void>.delayed(const Duration(seconds: 5));
    await cycle(firstRun: true);
    Timer.periodic(fast, (_) => tick());
    await Completer<void>().future;
  }

  void tick() {
    try {
      final now = DateTime.now().toUtc();
      final folder = SyncFolder(LocalConfig.load().folder);
      final was = _watchers.isNotEmpty;
      _watchers = folder.watchers(now);
      final due = _watchers.isNotEmpty ? fast : interval;
      // A little slack so a 10 s timer doesn't miss a 60 s cycle by a hair.
      if (_lastCycle == null || now.difference(_lastCycle!) >= due - const Duration(seconds: 2) || (_watchers.isNotEmpty && !was)) {
        cycle();
      }
    } catch (_) {
      cycle();
    }
  }

  bool _busy = false;

  /// Writes each attached thing's file. A thing seen before but not now is
  /// rewritten as disconnected, keeping its declared place in the hierarchy.
  /// Returns each thing's declared parent, connected or not.
  Map<String, String?> _writeThings(SyncFolder folder, String host, List<Report> things, DateTime now) {
    final seen = {for (final t in things) t.id};
    final parents = {for (final t in things) t.id: t.node?.parent};
    for (final t in things) {
      folder.writeThing(host, t);
    }
    for (final id in folder.thingIds(host)) {
      if (seen.contains(id)) continue;
      final f = File('${folder.thingDir(host, id)}${Platform.pathSeparator}now.json');
      Report? old;
      try {
        old = Report.tryParse(jsonDecode(f.readAsStringSync()));
      } catch (_) {}
      if (old == null) continue;
      parents[id] = old.node?.parent;
      if (old.connected == false) continue;
      folder.writeThing(host, Report(
        id: old.id, name: old.name, kind: old.kind, time: now, machine: old.machine,
        nextSeconds: old.nextSeconds, connected: false, node: old.node,
      ));
      folder.log(host, 'thing ${old.name} ($id) ${old.node?.type == 'container' || old.node?.type == 'compose-project' ? 'gone' : 'unplugged'}');
    }
    // UPS charge, load and so on: one history line a minute per thing.
    if (_lastThingHistory == null || now.difference(_lastThingHistory!) >= const Duration(seconds: 55)) {
      for (final t in things) {
        HistoryWriter(folder.thingDir(host, t.id), '${LocalConfig.dir}${Platform.pathSeparator}things${Platform.pathSeparator}${t.id}')
            .add(t.historyLine(), now);
      }
      _lastThingHistory = now;
    }
    return parents;
  }

  /// Applies requests for this device and its things, once each, and says
  /// in its own results folder and log what happened.
  Future<void> _applyRequests(SyncFolder folder, LocalConfig cfg, DateTime now) async {
    folder.pruneControls(cfg.id, now);
    for (final r in folder.requestsFor(cfg.id, now)) {
      if (folder.handled(cfg.id, r.id)) continue;
      ControlResult done(bool ok, String message, [String? value]) => ControlResult(id: r.id, ok: ok, message: message, value: value, time: DateTime.now().toUtc());
      ControlResult res;
      try {
        if (r.key == 'ups.alarm' && Platform.isLinux) {
          final v = {'off': 1, 'on': 2, 'quiet': 3}[r.value];
          var why = '';
          final now = v == null ? null : HidUps.setAlarm(v, (w) => why = w);
          res = now == null
              ? done(false, v == null ? 'unknown value ${r.value}' : why)
              : done(true, 'alarm ${{1: 'off', 2: 'on', 3: 'quiet'}[now]}', {1: 'off', 2: 'on', 3: 'quiet'}[now]);
        } else if (r.key == 'update' && r.target == cfg.id && Updater.canUpdate) {
          res = done(true, await Updater.start(folder, cfg));
        } else {
          res = done(false, 'this device has no setting ${r.key}');
        }
      } catch (e) {
        res = done(false, '$e');
      }
      folder.writeResult(cfg.id, res);
      folder.log(cfg.id, '${res.ok ? 'control' : 'ERROR control'} ${r.key}=${r.value} for ${r.target}, asked by ${r.fromName}: ${res.message}');
      if (r.key == 'update' && res.ok && Updater.exitsAfterStart) exit(0);
    }
  }

  Future<void> cycle({bool firstRun = false}) async {
    if (_busy) return; // never overlap cycles
    _busy = true;
    _lastCycle = DateTime.now().toUtc();
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
      // Changes other devices asked for (control.dart), before collecting,
      // so the report written below already shows the new value.
      // Linux package updated underneath us (dnf/apt or the app's Install): the running binary is the old, deleted one. Exit; systemd
      // starts the new one.
      if (Updater.replaced) {
        folder.log(cfg.id, 'update: a new version was installed; restarting the collector');
        exit(0);
      }
      await _applyRequests(folder, cfg, DateTime.now().toUtc());
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
        // The other system of a dual-boot machine that is running its twin:
        // it is not booted, so there is nothing to reach. Not a problem.
        final twinUp = o.machine != null &&
            others.values.any((x) => x.id != o.id && x.machine == o.machine && x.time.isAfter(o.time)) &&
            seen > o.nextSeconds * 3 + 120;
        if (twinUp || (o.machine != null && o.machine == _machine)) return;
        // Both at once: an unreachable device costs one timeout, not two.
        final (tcp, ms) = await (tcpProbe(o.tailscaleIp!), host.ping(o.tailscaleIp!)).wait;
        peers[o.id] = PeerView(tcp: tcp, pingMs: ms, seenAge: seen);
      }));

      List<Reading> extra;
      try {
        extra = await _extras.collect(now);
      } catch (_) {
        extra = const [];
      }

      // Things plugged into this machine (UPS, audio interface), Linux only.
      var things = <Report>[];
      if (Platform.isLinux) {
        try {
          things = await _things.collect(cfg.id, _machine, now, _watchers.isNotEmpty ? fast.inSeconds : interval.inSeconds);
        } catch (e) {
          folder.log(cfg.id, 'ERROR things scan failed: $e');
        }
        try {
          things.addAll(await _containers.collect(cfg.id, _machine, ip, now, _watchers.isNotEmpty ? fast.inSeconds : interval.inSeconds));
        } catch (e) {
          folder.log(cfg.id, 'ERROR containers scan failed: $e');
        }
      }

      final parents = _writeThings(folder, cfg.id, things, now);

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
        nextSeconds: _watchers.isNotEmpty ? fast.inSeconds : interval.inSeconds,
        watchedBy: _watchers,
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
        readings: extra,
        self: SelfCost(rssMb: rss, runMs: sw.elapsedMilliseconds, cpuMs: cycleCpu, over: over),
        controls: [if (Updater.canUpdate) Updater.control(appVersion)],
        node: NodeDecl(
          id: cfg.id,
          type: 'device',
          children: [for (final e in parents.entries) if (e.value == cfg.id) e.key]..sort(),
          links: [
            ...Things.hostLinks(things),
            // Other systems of this same machine (dual boot), from their files.
            for (final o in others.values)
              if (o.id != cfg.id && _machine != null && o.machine == _machine && o.kind != 'android')
                NodeLink(to: o.id, via: 'dual-boot', dir: 'both', detail: 'same machine, ${o.os ?? o.name}; only one runs at a time'),
          ],
        ),
      );
      folder.writeNow(r);
      final hw = HistoryWriter(folder.deviceDir(cfg.id), LocalConfig.dir);
      // History stays at one line a minute however fast the live rate is.
      if (_lastHistory == null || now.difference(_lastHistory!) >= const Duration(seconds: 55)) {
        hw.add(r.historyLine(), now);
        _lastHistory = now;
      }
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
