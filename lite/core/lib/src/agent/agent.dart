import 'dart:async';
import 'dart:convert';
import 'dart:io';

import '../config.dart';
import '../folder.dart';
import '../format.dart';
import '../history.dart';
import '../report.dart';
import '../status.dart' show freshLimit, ago;
import '../topology.dart';
import '../update.dart';
import '../version.dart';
import 'extras.dart';
import 'linux.dart';
import 'sample.dart';
import 'things.dart';
import 'containers.dart';
import 'smart.dart';
import 'windows.dart';
import 'updater.dart';
import 'hid_ups.dart';
import '../control.dart';
import '../patterns.dart';
import '../model.dart';
import '../events.dart';
import '../integrity.dart';
import '../unusual.dart';
import '../rules.dart';
import '../rule_eval.dart';
import '../notify.dart';
import '../fetch.dart';
import '../ai.dart';
import '../discover.dart';
import '../photos.dart';
import 'desktop_notify.dart';
import '../project.dart';
import '../products.dart';

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
    final found = <(String, String)>[];
    for (final i in await NetworkInterface.list(type: InternetAddressType.IPv4)) {
      for (final a in i.addresses) {
        if (isTailscaleIp(a)) found.add((i.name, a.address));
      }
    }
    return pickTailscale(found);
  } catch (_) {}
  return null;
}

/// 100.64/10 is also what mobile carriers and some ISPs hand out (CGNAT):
/// the Pixel on 2026-11-27 had rmnet16 100.86.5.58 (the carrier) next to
/// tun0 100.88.178.60 (Tailscale), and published the carrier's, so nobody
/// could reach it. Prefer the VPN interface; never a cellular one.
String? pickTailscale(List<(String, String)> found) {
  bool vpn(String n) => n.startsWith('tailscale') || n.startsWith('tun') || n.startsWith('utun') || n.toLowerCase().contains('tailscale');
  bool cell(String n) => RegExp(r'^(rmnet|ccmni|pdp|wwan|clat|v4-rmnet|seth|ccinet|wwp)').hasMatch(n);
  return found.where((f) => vpn(f.$1)).firstOrNull?.$2 ?? found.where((f) => !cell(f.$1)).firstOrNull?.$2;
}

Future<String> tcpProbe(String host, {Duration timeout = const Duration(seconds: 3)}) async => (await tcpProbeTimed(host, timeout: timeout)).$1;

/// [tcpProbe] plus how long the TCP handshake took (ms): the time to the
/// peer's answer, accepted or refused. A second measure of latency that
/// needs no ICMP, so it works where ping can't (containers).
Future<(String, int?)> tcpProbeTimed(String host, {Duration timeout = const Duration(seconds: 3)}) async {
  final sw = Stopwatch()..start();
  try {
    final s = await Socket.connect(host, syncthingPort, timeout: timeout);
    final ms = sw.elapsedMilliseconds;
    s.destroy();
    return ('open', ms);
  } on SocketException catch (e) {
    final code = e.osError?.errorCode;
    // ECONNREFUSED: Linux 111, Windows 10061, macOS 61.
    if (code == 111 || code == 10061 || code == 61) return ('refused', sw.elapsedMilliseconds);
    return ('timeout', null);
  } catch (_) {
    return ('error', null);
  }
}

/// Which running copy of the collector is the one: the newest to start.
///
/// The lock in bin/monitor_agent.dart should already keep it to one copy,
/// but on 2026-11-26 Wilson's Windows laptop ran several at once for two
/// hours (interleaved hourly summaries, budget lines several per second, and
/// 315 failed now.json writes as the copies collided). So each copy also
/// writes its own token here at start and stops as soon as a newer copy has
/// replaced it. It costs one small read a tick.
class InstanceClaim {
  InstanceClaim(this.path, {int? pid, DateTime? started}) : token = '${pid ?? 0} ${(started ?? DateTime.now()).microsecondsSinceEpoch}';
  final String path;
  final String token;

  void take() {
    final tmp = File('$path.tmp');
    tmp.writeAsStringSync(token, flush: true);
    tmp.renameSync(path);
  }

  /// False only when another copy's token is there. A missing or unreadable
  /// file never stops this copy.
  bool held() {
    try {
      final f = File(path);
      if (!f.existsSync()) return true;
      final t = f.readAsStringSync().trim();
      return t.isEmpty || t == token;
    } catch (_) {
      return true;
    }
  }
}

class Agent {
  Agent({Host? host, this.claim}) : host = host ?? (Platform.isWindows ? WindowsHost() : LinuxHost());
  final Host host;

  /// When set, this copy stops once a newer copy has taken over.
  final InstanceClaim? claim;
  final interval = const Duration(seconds: 60);

  int _seq = 0;
  String? _machine;
  String? _os;
  String? _lastFolder;
  DateTime? _lastPrune;
  DateTime? _lastSummary;
  int _cycles = 0, _maxCycleMs = 0;

  /// Where cycles spend their time and memory (row 2 of docs/ROADMAP.md):
  /// per step, the slowest time this hour and the total memory it added.
  /// Written in the hourly "ok" line as steps[name=maxMs/+MB ...].
  final Map<String, (int, double)> _steps = {};
  Stopwatch? _stepSw;
  double _stepRss = 0;
  void _stepStart() {
    _stepSw = Stopwatch()..start();
    _stepRss = ProcessInfo.currentRss / (1024 * 1024);
  }

  void _step(String name) {
    final ms = _stepSw?.elapsedMilliseconds ?? 0;
    final rss = ProcessInfo.currentRss / (1024 * 1024);
    final grew = rss - _stepRss;
    final old = _steps[name] ?? (0, 0.0);
    _steps[name] = (ms > old.$1 ? ms : old.$1, old.$2 + (grew > 0 ? grew : 0));
    _stepSw = Stopwatch()..start();
    _stepRss = rss;
  }

  String _stepsText() => _steps.entries.map((e) => '${e.key}=${e.value.$1}ms/+${e.value.$2.toStringAsFixed(1)}MB').join(' ');
  double _maxRss = 0, _minRss = 0;

  static const fast = Duration(seconds: watchIntervalSeconds);
  DateTime? _lastCycle;
  DateTime? _lastHistory;
  List<String> _watchers = const [];
  DateTime? _unusualAt;
  List<Reading> _unusual = const [];
  final _unusualLimit = UnusualLimiter();
  final _extras = Extras();
  String? _ctWhyLogged;
  final _things = Things();
  final _containers = Containers();
  final _smart = Smart();
  DateTime? _fetchAt;
  bool _fetching = false;
  bool _discovering = false;
  DateTime? _aiAt;
  List<String> _aiModels = const [];
  bool _aiBusy = false;
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
    if (claim != null && !claim!.held()) {
      try {
        final cfg = LocalConfig.load();
        SyncFolder(cfg.folder).log(cfg.id, 'stop agent pid $pid: a newer copy of the collector took over');
      } catch (_) {}
      exit(0);
    }
    try {
      final now = DateTime.now().toUtc();
      final folder = SyncFolder(LocalConfig.load().folder);
      final was = _watchers.isNotEmpty;
      _watchers = folder.watchers(now);
      final due = _watchers.isNotEmpty ? fast : interval;
      // A little slack so a 10 s timer doesn't miss a 60 s cycle by a hair.
      if (_lastCycle == null || now.difference(_lastCycle!) >= due - const Duration(seconds: 2) || (_watchers.isNotEmpty && !was)) {
        cycle();
      } else {
        _autoUpdate(now);
      }
    } catch (_) {
      cycle();
    }
  }

  bool _busy = false;
  EventState? _eventState;
  bool _settled = false;
  RuleEngine? _rules;
  NotifyInbox? _inbox;
  bool _defaultsChecked = false;
  String _ruleProblems = '';
  DateTime? _prevCycleAt;

  /// Automatic update (Windows): first check [autoUpdateFirstDelay] after
  /// start, then every [autoUpdateEvery]. Runs beside the cycles, not in
  /// one, so a download never counts against the cycle budget. Waits while
  /// the app window is open here, so nobody's screen closes under them.
  final DateTime _started = DateTime.now().toUtc();
  DateTime? _lastUpdateCheck;
  bool _updating = false;

  Future<void> _autoUpdate(DateTime now) async {
    if (!Updater.canUpdate || _updating || _busy) return;
    if (now.difference(_lastUpdateCheck ?? _started.subtract(autoUpdateEvery - autoUpdateFirstDelay)) < autoUpdateEvery) return;
    _updating = true;
    _lastUpdateCheck = now;
    try {
      final cfg = LocalConfig.load();
      final folder = SyncFolder(cfg.folder);
      if (!autoUpdateOn(cfg.folder)) return;
      if (folder.watchedHere(cfg.id, now)) {
        _lastUpdateCheck = now.subtract(autoUpdateEvery - const Duration(minutes: 30)); // look again in 30 min
        return;
      }
      String msg;
      try {
        msg = await Updater.start(folder, cfg, why: 'automatic update');
      } catch (e) {
        folder.log(cfg.id, 'ERROR update check: $e');
        return;
      }
      if (msg.startsWith('installing')) {
        folder.log(cfg.id, 'update: $msg; the installer restarts the collector');
        exit(0);
      }
    } catch (_) {
    } finally {
      _updating = false;
    }
  }

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
      folder.writeThing(
          host,
          Report(
            id: old.id,
            name: old.name,
            kind: old.kind,
            time: now,
            machine: old.machine,
            nextSeconds: old.nextSeconds,
            connected: false,
            node: old.node,
          ));
      folder.log(host, 'thing ${old.name} ($id) ${old.node?.type == 'container' || old.node?.type == 'compose-project' ? 'gone' : 'unplugged'}');
    }
    // UPS charge, load and so on: one history line a minute per thing.
    if (_lastThingHistory == null || now.difference(_lastThingHistory!) >= const Duration(seconds: 55)) {
      for (final t in things) {
        HistoryWriter(folder.thingDir(host, t.id), '${LocalConfig.dir}${Platform.pathSeparator}things${Platform.pathSeparator}${t.id}').add(t.historyLine(), now);
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
          res = now == null ? done(false, v == null ? 'unknown value ${r.value}' : why) : done(true, 'alarm ${{1: 'off', 2: 'on', 3: 'quiet'}[now]}', {1: 'off', 2: 'on', 3: 'quiet'}[now]);
        } else if (r.key == 'update' && r.target == cfg.id && Updater.canUpdate) {
          res = done(true, await Updater.start(folder, cfg));
        } else {
          res = done(false, 'this device has no setting ${r.key}');
        }
      } catch (e) {
        res = done(false, '$e');
      }
      folder.writeResult(cfg.id, res);
      if (res.ok) {
        appendEvents(folder.deviceDir(cfg.id), [
          Event(DateTime.now().toUtc(), 'control.applied', subject: r.target, detail: '${r.key}=${r.value}, asked by ${r.fromName}: ${res.message}')
        ]);
      }
      folder.log(cfg.id, '${res.ok ? 'control' : 'ERROR control'} ${r.key}=${r.value} for ${r.target}, asked by ${r.fromName}: ${res.message}');
      if (r.key == 'update' && res.ok && Updater.exitsAfterStart) exit(0);
    }
  }

  /// devices/<id>/patterns.json: what this device's history shows, kept as
  /// a small readable file (FORMAT.md "Patterns"). Rewritten every 6 hours.
  void _writePatterns(SyncFolder folder, String id, Map<String, Report> others, DateTime now) {
    try {
      final names = {for (final o in others.values) o.id: o.name};
      final up = {for (final o in others.values) o.id: hoursUp(readHourly(folder.deviceDir(o.id), now))};
      final dir = folder.deviceDir(id);
      final list = findPatterns(readDetail(dir, now), readHourly(dir, now), names: names, upHours: up);
      final sep = Platform.pathSeparator;
      final tmp = File('$dir$sep.syncthing.patterns.json.tmp');
      tmp.writeAsStringSync(const JsonEncoder.withIndent('  ').convert({
        'v': 1,
        't': now.millisecondsSinceEpoch ~/ 1000,
        'patterns': [for (final p in list) p.toJson()]
      }));
      tmp.renameSync('$dir${sep}patterns.json');
      recordPatterns(dir, list, now);
      // Attached things with history of their own (a UPS: load, power cuts,
      // runtime when full) get theirs next to their now.json.
      final events = readEvents(folder.root, now.subtract(const Duration(days: 7)), now, device: id);
      for (final t in folder.thingIds(id)) {
        final tdir = folder.thingDir(id, t);
        final tl = [
          ...findPatterns(readDetail(tdir, now), readHourly(tdir, now)),
          if (t.startsWith('ctr-')) ...restartsPattern(events, t, now),
        ];
        if (tl.isEmpty) continue;
        final ttmp = File('$tdir$sep.syncthing.patterns.json.tmp');
        ttmp.writeAsStringSync(const JsonEncoder.withIndent('  ').convert({
          'v': 1,
          't': now.millisecondsSinceEpoch ~/ 1000,
          'patterns': [for (final p in tl) p.toJson()]
        }));
        ttmp.renameSync('$tdir${sep}patterns.json');
        recordPatterns(tdir, tl, now);
      }
    } catch (e) {
      folder.log(id, 'ERROR patterns: $e');
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
      _stepStart();
      await _applyRequests(folder, cfg, DateTime.now().toUtc());
      _step('requests');
      final s = host.sample();
      _step('sample');
      final ip = await tailscaleIp();
      final stLocal = await tcpProbe('127.0.0.1', timeout: const Duration(seconds: 1));
      final others = folder.readReports()..remove(cfg.id);
      _step('read');
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
        final twinUp = o.machine != null && others.values.any((x) => x.id != o.id && x.machine == o.machine && x.time.isAfter(o.time)) && seen > o.nextSeconds * 3 + 120;
        if (twinUp || (o.machine != null && o.machine == _machine)) return;
        // Both at once: an unreachable device costs one timeout, not two.
        final (t, ms) = await (tcpProbeTimed(o.tailscaleIp!), host.ping(o.tailscaleIp!)).wait;
        peers[o.id] = PeerView(tcp: t.$1, pingMs: ms, tcpMs: t.$2, seenAge: seen);
      }));
      _step('peers');

      List<Reading> extra;
      try {
        extra = await _extras.collect(now);
      } catch (_) {
        extra = const [];
      }
      // A local Ollama (row 37), asked every 10 minutes; nothing when absent.
      if (_aiAt == null || now.difference(_aiAt!) >= const Duration(minutes: 10)) {
        _aiAt = now;
        final had = _aiModels.isNotEmpty;
        _aiModels = await localModels() ?? const [];
        if (_aiModels.isNotEmpty != had) folder.log(cfg.id, _aiModels.isEmpty ? 'ai: local models gone' : 'ai: local models ${_aiModels.join(', ')}');
      }
      if (_aiModels.isNotEmpty) {
        extra = [...extra, Reading(key: 'ai.models', label: 'Local AI models', value: _aiModels.join(', '), group: 'AI', src: 'measured')];
      }
      final ctWhy = extra.where((r) => r.key == 'ct.access').firstOrNull?.note;
      if (ctWhy != _ctWhyLogged) {
        if (ctWhy != null) folder.log(cfg.id, 'containers: $ctWhy');
        _ctWhyLogged = ctWhy;
      }
      // Unusual for this device (row 16): every 5 minutes, from its history.
      if (_unusualAt == null || now.difference(_unusualAt!) >= const Duration(minutes: 5)) {
        _unusualAt = now;
        try {
          final dir = folder.deviceDir(cfg.id);
          final from = now.millisecondsSinceEpoch ~/ 1000 - 3600;
          final recent = readDetail(dir, now, days: 1).where((l) => (l['t']! as num) >= from).toList();
          final found = _unusualLimit.apply(findUnusual(readHourly(dir, now), recent, now), now);
          for (final r in found) {
            if (!_unusual.any((o) => o.key == r.key)) folder.log(cfg.id, 'unusual: ${r.value}');
          }
          _unusual = found;
        } catch (e) {
          folder.log(cfg.id, 'ERROR unusual: $e');
        }
      }
      extra = [...extra, ..._unusual];
      _step('extras');

      // Things plugged into this machine (UPS, audio interface), Linux only.
      var things = <Report>[];
      if (Platform.isLinux) {
        try {
          things = await _things.collect(cfg.id, _machine, now, _watchers.isNotEmpty ? fast.inSeconds : interval.inSeconds);
        } catch (e) {
          folder.log(cfg.id, 'ERROR things scan failed: $e');
        }
        try {
          things.addAll(_smart.collect(cfg.id, _machine, now, interval.inSeconds));
        } catch (e) {
          folder.log(cfg.id, 'ERROR disk health read failed: $e');
        }
        try {
          things.addAll(await _containers.collect(cfg.id, _machine, ip, now, _watchers.isNotEmpty ? fast.inSeconds : interval.inSeconds));
        } catch (e) {
          folder.log(cfg.id, 'ERROR containers scan failed: $e');
        }
      }

      _step('things');
      final parents = _writeThings(folder, cfg.id, things, now);
      _step('thingfiles');
      try {
        final statePath = '${LocalConfig.dir}${Platform.pathSeparator}events-state.json';
        final before = _eventState ??= EventState.load(statePath);
        final after = EventState.of(app: appVersion, others: others, things: things, now: now);
        // Just started, or this machine slept: others' files lag behind.
        final settled = !firstRun && _prevCycleAt != null && now.difference(_prevCycleAt!) < const Duration(minutes: 3) && now.difference(_started) > const Duration(minutes: 5);
        _settled = settled;
        final ev = diffEvents(before, after, now, peers: settled);
        appendEvents(folder.deviceDir(cfg.id), ev);
        for (final e in ev) {
          folder.log(cfg.id, 'event ${e.type}${e.name != null ? ' ${e.name}' : ''}${e.detail != null ? ': ${e.detail}' : ''}');
        }
        if (ev.isNotEmpty || before.values.isEmpty || firstRun) after.save(statePath);
        _eventState = after;
        _prevCycleAt = now;
      } catch (e) {
        folder.log(cfg.id, 'ERROR events: $e');
      }
      _step('events');

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
          children: [
            for (final e in parents.entries)
              if (e.value == cfg.id) e.key
          ]..sort(),
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
      // A clock that jumped back (dual-boot hardware clock in local time,
      // then a time sync) must not stop history until it catches up.
      final back = hw.clockWentBack(now);
      if (back != null) folder.log(cfg.id, 'clock went back ${ago(back)}: earlier lines were stamped ahead (is the hardware clock in local time on another system of this machine?)');
      if (_lastHistory == null || now.isBefore(_lastHistory!) || now.difference(_lastHistory!) >= const Duration(seconds: 55)) {
        hw.add(r.historyLine(), now);
        _lastHistory = now;
      }
      _step('write');
      // Rules (row 19): only once others' files have caught up after a start
      // or a sleep, so nothing looks offline that isn't.
      if (_settled) {
        try {
          // Default rules (row 21), written once per folder by the one
          // computer that judges rules for others.
          if (!_defaultsChecked && ruleEvaluator({...others, cfg.id: r}, now) == cfg.id) {
            _defaultsChecked = true;
            final wrote = ensureDefaultRules(cfg.folder);
            if (wrote.isNotEmpty) folder.log(cfg.id, 'rules: added default rules (switched off): ${wrote.join(', ')}');
          }
          final set = RuleSet.load(cfg.folder);
          final probs = set.problems.entries.map((e) => '${e.key}: ${e.value.join('; ')}').join(' | ');
          if (probs != _ruleProblems) {
            if (probs.isNotEmpty) folder.log(cfg.id, 'rules not run: $probs');
            _ruleProblems = probs;
          }
          if (set.rules.isNotEmpty) {
            final fired = (_rules ??= RuleEngine('${LocalConfig.dir}${Platform.pathSeparator}rules-state.json'))
                .evaluate(root: cfg.folder, me: cfg.id, rules: set, reports: {...others, cfg.id: r}, things: things, now: now);
            for (final f in fired) {
              folder.log(cfg.id, 'rule ${f.rule.id} fired for ${f.subject}: ${f.why} -> ${f.did.join(', ')}');
            }
          }
        } catch (e) {
          folder.log(cfg.id, 'ERROR rules: $e');
        }
        _step('rules');
        // Optional AI (row 37): answer requests meant for this computer, off
        // the cycle.
        if (_aiModels.isNotEmpty && !_aiBusy) {
          _aiBusy = true;
          final f = folder, id = cfg.id;
          unawaited(answerAiRequests(cfg.folder, cfg.id, {...others, cfg.id: r}, _aiModels, now).then((lines) {
            for (final l in lines) {
              f.log(id, l);
            }
          }).catchError((Object e) {
            f.log(id, 'ERROR ai: $e');
          }).whenComplete(() => _aiBusy = false));
        }
      }
      // Notifications from rules, wherever they fired (row 20). Shown off
      // the cycle; a burst shows three and a count.
      try {
        final notes = (_inbox ??= NotifyInbox('${LocalConfig.dir}${Platform.pathSeparator}notify-state.json')).take(cfg.folder, cfg.id, now);
        final show = [
          ...notes.take(3).map((n) => (n.title, n.text)),
          if (notes.length > 3) ('Monitor App', '${notes.length - 3} more rules fired; see the app'),
        ];
        final f = folder, id = cfg.id;
        for (final (title, text) in show) {
          desktopNotify(title, text).then((why) => f.log(id, why == null ? 'notified: $title: $text' : 'notification not shown ($why): $title'));
        }
      } catch (e) {
        folder.log(cfg.id, 'ERROR notifications: $e');
      }
      _step('notify');
      if (over.isNotEmpty) folder.log(cfg.id, 'OVER BUDGET: ${over.join('; ')}');
      _cycles++;
      // Lowest memory seen this hour: near maxRss means a resident heap, far
      // below means short peaks (row 2).
      _minRss = _minRss == 0 || rss < _minRss ? rss : _minRss;
      _maxCycleMs = sw.elapsedMilliseconds > _maxCycleMs ? sw.elapsedMilliseconds : _maxCycleMs;
      _maxRss = rss > _maxRss ? rss : _maxRss;
      if (_lastSummary == null || now.difference(_lastSummary!) > const Duration(hours: 1)) {
        final seen = others.values
            .map((o) => '${o.name}:${peers[o.id]?.tcp ?? '-'}/${peers[o.id]?.pingMs ?? '-'}ms/tcp${peers[o.id]?.tcpMs ?? '-'}ms/'
                '${now.difference(o.time).inSeconds > freshLimit(o).inSeconds ? 'stale' : 'fresh'}')
            .join(' ');
        folder.log(cfg.id,
            'ok cycles=$_cycles maxCycle=${_maxCycleMs}ms maxRss=${_maxRss.toStringAsFixed(1)}MB minRss=${_minRss.toStringAsFixed(1)}MB ts=${ip ?? 'none'} st=$stLocal peers[$seen] steps[${_stepsText()}]${Extras.tailscaleProblem == null ? '' : ' ts-problem="${Extras.tailscaleProblem}"'}${host.pingProblem == null ? '' : ' ping-problem="${host.pingProblem}"'}');
        _lastSummary = now;
        _cycles = 0;
        _maxCycleMs = 0;
        _steps.clear();
        _maxRss = 0;
        _minRss = 0;
      }
      if (_lastPrune == null || now.difference(_lastPrune!) > const Duration(hours: 6)) {
        final ret = Retention.load(cfg.folder);
        final used = hw.prune(now, ret);
        pruneEvents(folder.deviceDir(cfg.id), now, ret.hourlyDays);
        folder.log(cfg.id, 'history ${(used / 1024).round()} KB');
        try {
          folder.log(cfg.id, checkHistory(folder.deviceDir(cfg.id), now, ret).line);
        } catch (e) {
          folder.log(cfg.id, 'ERROR history check: $e');
        }
        _lastPrune = now;
        _writePatterns(folder, cfg.id, others, now);
        try {
          final n = ModelStore(cfg.folder, cfg.id).resolveConflicts() + ProjectStore(cfg.folder, cfg.id).resolveConflicts() + ProductStore(cfg.folder, cfg.id).resolveConflicts();
          if (n > 0) folder.log(cfg.id, 'model: resolved $n conflict copies (newest kept, others in history)');
        } catch (e) {
          folder.log(cfg.id, 'ERROR model conflicts: $e');
        }
      }
      // Offers (row 31): only on the computer named in model/fetch.json,
      // once a day, in the background so the cycle stays in budget.
      if (!_fetching && (_fetchAt == null || now.difference(_fetchAt!) >= const Duration(hours: 24)) && fetcherDevice(cfg.folder) == cfg.id) {
        _fetchAt = now;
        _fetching = true;
        final f = folder, id = cfg.id, root = cfg.folder;
        unawaited(fetchOffers(root, ProductStore(root, id), sourcesFrom(LocalConfig.dir), now, me: id)
            // Front photos (row 31a) from makers' sites, same daily pass.
            .then((lines) async => [...lines, ...await updateFrontPhotos(root, id, ProductStore(root, id), now)])
            .then((lines) {
          for (final l in lines) {
            f.log(id, l);
          }
        }).catchError((Object e) {
          f.log(id, 'ERROR offers: $e');
        }).whenComplete(() => _fetching = false));
      }
      // "Find me one" (row 33): searches asked for in discover/requests,
      // on the same computer, in the background.
      if (!_discovering && Directory('${cfg.folder}/discover/requests').existsSync() && fetcherDevice(cfg.folder) == cfg.id) {
        _discovering = true;
        final f = folder, id = cfg.id;
        unawaited(answerDiscoverRequests(cfg.folder, id, sourcesFrom(LocalConfig.dir), now).then((lines) {
          for (final l in lines) {
            f.log(id, l);
          }
        }).catchError((Object e) {
          f.log(id, 'ERROR discover: $e');
        }).whenComplete(() => _discovering = false));
      }
      // Patterns, conflicts and background starts, so no growth goes
      // unattributed.
      _step('tail');
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
