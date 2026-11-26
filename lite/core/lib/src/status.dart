import 'dart:math' as math;

import 'report.dart';

/// How bad a status is. Colors in the UI: ok green, warn amber, fault red,
/// inactive gray.
enum Level { ok, inactive, warn, fault }

class DeviceStatus {
  DeviceStatus(this.report, this.level, this.title, this.detail, {required this.fresh, required this.age});
  final Report report;
  final Level level;

  /// Two to five words, e.g. "Online", "Syncthing stopped".
  final String title;

  /// One or two plain sentences saying what is known and how we know it.
  final String detail;
  final bool fresh;
  final Duration age;
}

class NetworkStatus {
  NetworkStatus(this.level, this.title, this.detail, {this.groups = const []});
  final Level level;
  final String title;
  final String detail;

  /// When the network is split: the groups of device names that can still
  /// reach each other.
  final List<List<String>> groups;
}

class Assessment {
  Assessment(this.devices, this.network);

  /// Ordered: this device first, then by name.
  final List<DeviceStatus> devices;
  final NetworkStatus network;
}

/// How long a report stays "fresh": two missed cycles plus a minute for
/// Syncthing to carry it, never less than 2.5 minutes.
/// After this long without a reading a device is shown as away (gray)
/// instead of down (red).
const ghostAfter = Duration(days: 3);

Duration freshLimit(Report r) => Duration(seconds: math.max(r.nextSeconds * 2 + 60, 150));

String ago(Duration d) {
  final s = d.inSeconds;
  if (s < 90) return '${math.max(s, 0)} s';
  if (s < 90 * 60) return '${(s / 60).round()} min';
  if (s < 36 * 3600) return '${(s / 3600).round()} h';
  return '${(s / 86400).round()} days';
}

String _names(Iterable<Report> rs) {
  final n = rs.map((r) => r.name).toList();
  if (n.isEmpty) return '';
  if (n.length == 1) return n.first;
  return '${n.sublist(0, n.length - 1).join(', ')} and ${n.last}';
}

/// Combines every device's own report and what each device sees of the
/// others into one plain-English status per device plus one for the network.
///
/// Signals: a report arrives only if Syncthing works between the two devices;
/// a probe answers only if Tailscale works. A refused probe means the device
/// is up but Syncthing isn't listening. Several observers agreeing tell a
/// device that is down apart from one that is merely cut off from one peer.
Assessment assess(Map<String, Report> reports, {String? myId, DateTime? now}) {
  final t = now ?? DateTime.now().toUtc();
  Duration ageOf(Report r) {
    final a = t.difference(r.time);
    return a.isNegative ? Duration.zero : a;
  }

  bool isFresh(Report r) => ageOf(r) <= freshLimit(r);
  final fresh = reports.values.where(isFresh).toList();

  final out = <DeviceStatus>[];
  for (final d in reports.values) {
    final age = ageOf(d);
    final f = isFresh(d);
    DeviceStatus st(Level l, String title, String detail) => DeviceStatus(d, l, title, detail, fresh: f, age: age);

    // An earlier install of the same device (reinstalled, or the app's data
    // was cleared): same hardware and system, or for phones the same name,
    // and a newer report exists. Shown gray as an old copy, never red.
    if (d.id != myId) {
      final newer = reports.values.where((o) =>
          o.id != d.id &&
          o.kind == d.kind &&
          o.time.isAfter(d.time) &&
          (d.kind == 'android' ? o.name == d.name : (d.machine != null && o.machine == d.machine && o.os == d.os)));
      if (newer.isNotEmpty) {
        out.add(st(Level.inactive, 'Old copy',
            'Looks like an earlier install of ${newer.first.name}: same ${d.kind == 'android' ? 'name' : 'machine and system'}, '
            'and its last reading was ${ago(age)} ago. You can hide it.'));
        continue;
      }
    }

    // Another OS on the same physical machine is running: this one is simply
    // not booted. Never a red flag.
    final sibling = d.machine == null
        ? null
        : fresh.where((o) => o.id != d.id && o.machine == d.machine && o.os != d.os).firstOrNull;
    if (!f && sibling != null) {
      out.add(st(Level.inactive, 'Not booted',
          'This machine is running ${sibling.os ?? sibling.name} right now, so ${d.os ?? 'this system'} is not running. That is normal.'));
      continue;
    }

    // Silent for days: a retired device or one that's been put away. Gray,
    // with when it was last seen, rather than an alarm forever.
    if (!f && d.id != myId && age > ghostAfter) {
      out.add(st(Level.inactive, 'Not seen for ${ago(age)}',
          'Its last reading arrived ${ago(age)} ago (${d.time.toLocal().toString().substring(0, 16)}). '
          'If it is retired you can hide it; it reappears on its own if it ever reports again.'));
      continue;
    }

    final observers = fresh.where((o) => o.id != d.id && (d.machine == null || o.machine != d.machine));
    final views = <Report, PeerView>{
      for (final o in observers)
        if (o.peers[d.id] != null) o: o.peers[d.id]!,
    };
    final reachBy = views.entries.where((e) => e.value.reachable).map((e) => e.key).toList();
    final unreachBy = views.entries.where((e) => e.value.unreachable).map((e) => e.key).toList();
    final limit = freshLimit(d).inSeconds;
    final notReceiving = views.entries
        .where((e) => e.value.seenAge == null || e.value.seenAge! > limit)
        .map((e) => e.key)
        .toList();

    if (d.id == myId) {
      if (!f) {
        out.add(st(Level.fault, 'Not collecting',
            'This device last wrote its readings ${ago(age)} ago. ${d.kind == 'android' ? 'Android may be stopping Monitor in the background: open Setup and allow unrestricted battery use.' : 'The Monitor background program is not running.'}'));
      } else if (d.syncthingLocal == false) {
        out.add(st(Level.fault, 'Syncthing not running',
            'Syncthing is not running on this device, so its readings cannot reach the others and theirs cannot arrive here.'));
      } else if (d.tailscaleIp == null) {
        out.add(st(Level.warn, 'Tailscale off',
            'This device has no Tailscale address, so it cannot check whether the other devices are alive. Readings still sync through Syncthing.'));
      } else if (d.peers.isNotEmpty && d.peers.values.every((p) => !p.reachable)) {
        final othersFresh = fresh.where((o) => o.id != d.id).isNotEmpty;
        out.add(st(othersFresh ? Level.warn : Level.fault, othersFresh ? 'Can\'t reach others' : 'Cut off',
            othersFresh
                ? 'This device cannot reach any other device over Tailscale, but readings still arrive through Syncthing.'
                : 'This device cannot reach any other device over Tailscale and no fresh readings are arriving. It looks cut off from the network.'));
      } else {
        out.add(st(Level.ok, 'Online', 'This device is collecting and its readings are being shared.'));
      }
      continue;
    }

    if (f) {
      if (reachBy.isEmpty && unreachBy.isNotEmpty) {
        out.add(st(Level.warn, 'Tailscale unreachable',
            'Its readings still arrive through Syncthing, but ${_names(unreachBy)} cannot reach it over Tailscale. Tailscale may be off on ${d.name}.'));
      } else if (notReceiving.isNotEmpty && notReceiving.length < views.length) {
        out.add(st(Level.warn, 'Partly synced',
            'Readings reach this device, but ${_names(notReceiving)} ${notReceiving.length == 1 ? 'is' : 'are'} not receiving them through Syncthing.'));
      } else {
        out.add(st(Level.ok, 'Online',
            reachBy.isEmpty ? 'Its readings are arriving through Syncthing.' : 'Readings arriving through Syncthing; ${_names(reachBy)} can reach it over Tailscale.'));
      }
      continue;
    }

    // Stale: its readings stopped arriving. Use the other devices' probes to
    // tell "down" from "Syncthing stopped".
    if (reachBy.isNotEmpty) {
      final anyOpen = reachBy.any((o) => views[o]!.tcp == 'open');
      final refused = reachBy.where((o) => views[o]!.tcp == 'refused').toList();
      if (!anyOpen && refused.isNotEmpty) {
        out.add(st(Level.fault, 'Syncthing stopped',
            '${d.name} answers over Tailscale, but Syncthing is not listening on it, so its readings stopped ${ago(age)} ago. (Seen by ${_names(refused)}.)'));
      } else if (d.kind == 'android') {
        out.add(st(Level.warn, 'Reachable, not syncing',
            '${d.name} is on the network (${_names(reachBy)} can reach it) but its readings stopped ${ago(age)} ago. Syncthing-Fork may be paused, for example set to run only while charging, or Android put Monitor to sleep.'));
      } else {
        out.add(st(Level.warn, 'Reachable, not syncing',
            '${d.name} is on the network (${_names(reachBy)} can reach it) but its readings stopped ${ago(age)} ago. Syncthing may be paused, or the Monitor background program stopped.'));
      }
      continue;
    }
    if (unreachBy.isNotEmpty) {
      final b = d.battery;
      final flat = b?.pct != null && b!.pct! <= 10 && b.charging != true;
      out.add(st(Level.fault, flat ? 'Offline, battery flat?' : 'Offline',
          '${_names(unreachBy)} cannot reach ${d.name} over Tailscale and its readings stopped ${ago(age)} ago. '
          '${flat ? 'Its battery was at ${b.pct!.round()}% and not charging, so it probably ran out.' : 'It is off, asleep without network, or cut off from the network.'}'));
      continue;
    }
    out.add(st(Level.warn, 'No recent data',
        'Last update ${ago(age)} ago, and no other device has been able to check on it since.'));
  }

  out.sort((a, b) {
    if (a.report.id == myId) return -1;
    if (b.report.id == myId) return 1;
    return a.report.name.toLowerCase().compareTo(b.report.name.toLowerCase());
  });
  return Assessment(out, _network(out, fresh, myId));
}

NetworkStatus _network(List<DeviceStatus> devices, List<Report> fresh, String? myId) {
  final active = devices.where((d) => d.level != Level.inactive).toList();
  if (active.isEmpty) {
    return NetworkStatus(Level.warn, 'No devices yet',
        'No readings have arrived. Open Setup to share the Monitor folder in Syncthing on each device.');
  }

  // Tailscale reachability graph among devices that are alive (fresh, or
  // reachable by someone) and that someone has actually probed.
  final alive = <String>{};
  final edges = <String, Set<String>>{};
  void edge(String a, String b) {
    edges.putIfAbsent(a, () => {}).add(b);
    edges.putIfAbsent(b, () => {}).add(a);
  }

  final probed = <String>{};
  for (final o in fresh) {
    alive.add(o.id);
    o.peers.forEach((id, v) {
      if (v.reachable || v.unreachable) {
        probed.add(id);
        probed.add(o.id);
      }
      if (v.reachable) {
        alive.add(id);
        edge(o.id, id);
      }
    });
  }
  final names = {for (final d in devices) d.report.id: d.report.name};
  final nodes = alive.where((id) => probed.contains(id) && names.containsKey(id) &&
      devices.firstWhere((d) => d.report.id == id).level != Level.inactive).toList();
  final seen = <String>{};
  final groups = <List<String>>[];
  for (final n in nodes) {
    if (seen.contains(n)) continue;
    final g = <String>[];
    final stack = [n];
    seen.add(n);
    while (stack.isNotEmpty) {
      final x = stack.removeLast();
      g.add(x);
      for (final y in edges[x] ?? const <String>{}) {
        if (nodes.contains(y) && seen.add(y)) stack.add(y);
      }
    }
    groups.add(g);
  }
  if (groups.length > 1) {
    final named = groups.map((g) => g.map((id) => names[id]!).toList()..sort()).toList();
    final mine = myId == null ? null : groups.indexWhere((g) => g.contains(myId));
    final text = named.map((g) => g.join(' + ')).join('  |  ');
    return NetworkStatus(Level.fault, 'Network split',
        'Over Tailscale these groups can reach each other only within the group: $text.'
        '${mine != null && mine >= 0 && groups[mine].length == 1 ? ' This device is on its own.' : ''}',
        groups: named);
  }

  final away = devices.length - active.length;
  final awayText = away == 0 ? '' : ' $away more ${away == 1 ? 'is' : 'are'} away or old copies.';
  final ok = active.where((d) => d.level == Level.ok).length;
  final worst = active.map((d) => d.level).reduce((a, b) => a.index >= b.index ? a : b);
  if (ok == active.length) {
    return NetworkStatus(Level.ok, active.length == 1 ? 'Only this device so far' : (active.length == 2 ? 'Both devices online' : 'All ${active.length} devices online'),
        (active.length == 1 ? 'Share the Monitor folder with your other devices in Syncthing to see them here.' : 'Every device is reporting and reachable.') + awayText);
  }
  final bad = active.where((d) => d.level != Level.ok).map((d) => '${d.report.name}: ${d.title}');
  return NetworkStatus(worst, '$ok of ${active.length} devices online', '${bad.join('. ')}.$awayText');
}
