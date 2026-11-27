import 'dart:math' as math;

import 'project.dart';

/// Physical constraints for assemblies (roadmap row 25): fit (all three
/// dimensions, mounting standard, clearances), weight, power per outlet or
/// supply, heat against cooling and airflow direction, cable length and
/// bend room. FORMAT.md "Physical constraints".
///
/// Everything is read from fields the assembly and its placements already
/// carry (unknown keys are kept by the format), so nothing old breaks. Each
/// finding says how sure it is: `verified` (from declared or measured
/// values), `estimated` (a rule of thumb was used) or `unknown` (a value it
/// needs is missing, and which). A rule of thumb never certifies cooling,
/// electrical safety or stability.
class Finding {
  Finding(this.topic, this.level, this.evidence, this.text);

  /// `fit`, `mount`, `power`, `heat`, `cable`.
  final String topic;

  /// `fail`, `warn`, `ok` or `unknown`.
  final String level;

  /// `verified`, `estimated` or `unknown`.
  final String evidence;
  final String text;
  @override
  String toString() => '$topic $level ($evidence): $text';
}

const _uMm = 44.45;

Qty? _q(Map<String, Object?> m, String k) => Qty.fromJson(m[k]);
String _n(double v) => v == v.roundToDouble() ? v.round().toString() : v.toStringAsFixed(1);
String _ev(Iterable<Qty?> qs) => qs.any((q) => q?.src == 'estimated') ? 'estimated' : 'verified';

/// Heat a part gives off: declared `heat`, else its power draw times
/// (1 - `efficiency`) when that is declared (an amplifier sends the rest to
/// its speakers), else all of its power draw (estimated, the worst case).
(double, String)? heatOf(Placement p) {
  final h = _q(p.extra, 'heat')?.base('power');
  if (h != null) return (h, _ev([_q(p.extra, 'heat')]));
  final w = p.power?.base('power');
  if (w == null) return null;
  final eff = p.extra['efficiency'];
  if (eff is num && eff > 0 && eff < 1) return (w * (1 - eff), 'estimated');
  return (w, 'estimated');
}

List<Finding> checkPhysical(Assembly a) {
  final out = <Finding>[];
  final ax = a.extra;
  final aw = _q(ax, 'width')?.base('length'), ah = _q(ax, 'heightMm')?.base('length'), ad = a.depth?.base('length');

  // Mounting standard.
  final mount = ax['mount'];
  for (final p in a.placements) {
    final pm = p.extra['mount'];
    if (mount is String && pm is String && pm != mount && pm != 'shelf') {
      out.add(Finding('mount', 'fail', 'verified', '${p.name} mounts on $pm; the ${a.type} is $mount'));
    }
  }

  // Fit in a box (enclosure, cabinet, shelf): all three dimensions, then
  // again with the clearances its cooling needs.
  if (const {'enclosure', 'cabinet', 'shelf'}.contains(a.type)) {
    for (final p in a.placements) {
      final dims = {'width': (_q(p.extra, 'width'), aw), 'height': (_q(p.extra, 'heightMm'), ah), 'depth': (p.depth, ad)};
      final missing = [for (final e in dims.entries) if (e.value.$1?.base('length') == null || e.value.$2 == null) e.key];
      if (missing.isNotEmpty) {
        out.add(Finding('fit', 'unknown', 'unknown', '${p.name}: no ${missing.join(', ')} for it or the ${a.type}'));
        continue;
      }
      final too = [for (final e in dims.entries) if (e.value.$1!.base('length')! > e.value.$2!) '${e.key} ${_n(e.value.$1!.base('length')!)} mm > ${_n(e.value.$2!)} mm'];
      if (too.isNotEmpty) {
        out.add(Finding('fit', 'fail', 'verified', '${p.name} does not fit: ${too.join(', ')}'));
        continue;
      }
      final c = p.extra['clearance'];
      final need = <String, double>{};
      if (c is Map) {
        for (final side in const ['front', 'rear', 'top', 'sides']) {
          final q = Qty.fromJson(c[side])?.base('length');
          if (q != null) need[side] = q;
        }
      }
      final room = {
        'front': ad! - p.depth!.base('length')!, 'rear': ad - p.depth!.base('length')!,
        'top': ah! - _q(p.extra, 'heightMm')!.base('length')!, 'sides': (aw! - _q(p.extra, 'width')!.base('length')!) / 2,
      };
      final front = need['front'] ?? 0, rear = need['rear'] ?? 0;
      final short = [
        if (front + rear > room['front']!) 'front and back need ${_n(front + rear)} mm, there is ${_n(room['front']!)} mm',
        if ((need['top'] ?? 0) > room['top']!) 'top needs ${_n(need['top']!)} mm, there is ${_n(room['top']!)} mm',
        if ((need['sides'] ?? 0) > room['sides']!) 'each side needs ${_n(need['sides']!)} mm, there is ${_n(room['sides']!)} mm',
      ];
      out.add(short.isEmpty
          ? Finding('fit', 'ok', 'verified', '${p.name} fits${need.isEmpty ? ' (no clearances declared)' : ' with its clearances'}')
          : Finding('fit', 'fail', 'verified', '${p.name} fits, but not with the room its cooling needs: ${short.join('; ')}'));
    }
  }

  // Flat surfaces (pedalboard, shelf, desk): 2D bounds and overlaps from x/y.
  final flat = [for (final p in a.placements) if (_q(p.extra, 'x') != null && _q(p.extra, 'y') != null && _q(p.extra, 'width') != null && p.depth != null) p];
  for (final p in flat) {
    final x = _q(p.extra, 'x')!.base('length')!, y = _q(p.extra, 'y')!.base('length')!, w = _q(p.extra, 'width')!.base('length')!, d = p.depth!.base('length')!;
    if ((aw != null && x + w > aw) || (ad != null && y + d > ad) || x < 0 || y < 0) out.add(Finding('fit', 'fail', 'verified', '${p.name} hangs over the edge of the ${a.type}'));
  }
  for (var i = 0; i < flat.length; i++) {
    for (var j = i + 1; j < flat.length; j++) {
      final p = flat[i], q = flat[j];
      double v(Placement z, String k) => (k == 'depth' ? z.depth : _q(z.extra, k))!.base('length')!;
      if (v(p, 'x') < v(q, 'x') + v(q, 'width') && v(q, 'x') < v(p, 'x') + v(p, 'width') && v(p, 'y') < v(q, 'y') + v(q, 'depth') && v(q, 'y') < v(p, 'y') + v(p, 'depth')) {
        out.add(Finding('fit', 'fail', 'verified', '${p.name} and ${q.name} overlap'));
      }
    }
  }

  // Eurorack: HP per row, and rails against the supply.
  if (a.type == 'eurorack' || mount == 'eurorack') {
    final rowHp = ax['hp'];
    final rows = <int, List<Placement>>{};
    for (final p in a.placements) {
      if (p.extra['hp'] is int) (rows[(p.extra['row'] as int?) ?? 1] ??= []).add(p);
    }
    for (final r in rows.entries) {
      final used = r.value.fold<int>(0, (s, p) => s + (p.extra['hp'] as int));
      if (rowHp is int && used > rowHp) out.add(Finding('fit', 'fail', 'verified', 'row ${r.key}: modules take $used HP of $rowHp'));
    }
    final psu = ax['psu'];
    if (psu is Map) {
      for (final rail in const ['+12', '-12', '+5']) {
        final cap = psu[rail];
        if (cap is! num) continue;
        var sum = 0.0, unknown = <String>[];
        for (final p in a.placements) {
          final ma = (p.extra['ma'] as Map?)?[rail];
          if (ma is num) {
            sum += ma;
          } else if (rail != '+5') {
            unknown.add(p.name);
          }
        }
        if (sum > cap) {
          out.add(Finding('power', 'fail', 'verified', '$rail V rail: modules draw ${_n(sum)} mA, the supply gives $cap mA'));
        } else if (sum > cap * 0.8) {
          out.add(Finding('power', 'warn', 'verified', '$rail V rail at ${(100 * sum / cap).round()} % (keep about 20 % spare)'));
        }
        if (unknown.isNotEmpty) out.add(Finding('power', 'unknown', 'unknown', '$rail V draw unknown for ${unknown.join(', ')}'));
      }
    }
  }

  // Power per outlet or supply output: voltage, polarity, total current or
  // watts.
  final outlets = ax['outlets'];
  if (outlets is List) {
    for (final o in outlets.whereType<Map>()) {
      final id = '${o['id']}';
      final users = [for (final p in a.placements) if (p.extra['outlet'] == id) p];
      if (users.isEmpty) continue;
      for (final p in users) {
        if (o['v'] is num && p.extra['v'] is num && o['v'] != p.extra['v']) out.add(Finding('power', 'fail', 'verified', '${p.name} needs ${p.extra['v']} V; outlet $id gives ${o['v']} V'));
        if (o['polarity'] != null && p.extra['polarity'] != null && o['polarity'] != p.extra['polarity']) {
          out.add(Finding('power', 'fail', 'verified', '${p.name} needs ${p.extra['polarity']}; outlet $id is ${o['polarity']}'));
        }
      }
      if (o['ma'] is num) {
        final known = [for (final p in users) if (p.extra['ma'] is num) (p.extra['ma'] as num).toDouble()];
        final sum = known.fold(0.0, (s, x) => s + x);
        if (sum > (o['ma'] as num)) out.add(Finding('power', 'fail', 'verified', 'outlet $id: ${_n(sum)} mA drawn, it gives ${o['ma']} mA'));
        if (known.length < users.length) out.add(Finding('power', 'unknown', 'unknown', 'outlet $id: current unknown for ${users.length - known.length} part(s)'));
      }
      final max = Qty.fromJson(o['maxPower'])?.base('power');
      if (max != null) {
        final sum = users.fold(0.0, (s, p) => s + (p.power?.base('power') ?? 0));
        if (sum > max) out.add(Finding('power', 'fail', _ev(users.map((p) => p.power)), 'outlet $id: ${_n(sum)} W on a ${_n(max)} W outlet'));
      }
    }
  }

  // Heat: total against what the space can shed, and each part's airflow.
  final heats = [for (final p in a.placements) (p, heatOf(p))];
  final noHeat = [for (final h in heats) if (h.$2 == null) h.$1.name];
  final total = heats.fold(0.0, (s, h) => s + (h.$2?.$1 ?? 0));
  final heatEv = heats.any((h) => h.$2?.$2 == 'estimated') ? 'estimated' : 'verified';
  final vent = ax['ventilation']; // open, vented, closed
  final cooling = _q(ax, 'cooling')?.base('power');
  double? canShed;
  var shedEv = 'verified';
  if (cooling != null) {
    canShed = cooling;
  } else if (vent == 'closed' && aw != null && ah != null && ad != null) {
    // A closed box sheds about 5 W per m² of surface per °C; allow a 10 °C
    // rise inside (rule of thumb).
    final area = 2 * (aw * ah + aw * ad + ah * ad) / 1e6;
    canShed = area * 5 * 10;
    shedEv = 'estimated';
  }
  if (total > 0 && canShed != null) {
    final ev = heatEv == 'estimated' || shedEv == 'estimated' ? 'estimated' : 'verified';
    if (total > canShed) {
      out.add(Finding('heat', 'fail', ev, '${_n(total)} W of heat; the ${a.type} can shed about ${_n(canShed)} W${vent == 'closed' ? ' closed' : ''}${cooling == null ? ' (rule of thumb: 10 °C rise)' : ''}'));
    } else {
      out.add(Finding('heat', 'ok', ev, '${_n(total)} W of heat, about ${_n(canShed)} W can be shed'));
    }
  } else if (vent == 'closed' && total > 0) {
    out.add(Finding('heat', 'unknown', 'unknown', '${_n(total)} W of heat in a closed ${a.type} of unknown size'));
  }
  if (noHeat.isNotEmpty && (vent == 'closed' || cooling != null)) out.add(Finding('heat', 'unknown', 'unknown', 'no power or heat figure for ${noHeat.join(', ')}'));
  final rearOpen = ax['rearOpen'] != false, frontOpen = ax['frontOpen'] != false;
  for (final p in a.placements) {
    final flow = p.extra['airflow'];
    final blocked = switch (flow) {
      'front-to-back' => !rearOpen ? 'its exhaust at the back faces a closed panel' : (!frontOpen ? 'its intake at the front faces a closed panel' : null),
      'back-to-front' => !frontOpen ? 'its exhaust at the front faces a closed panel' : null,
      'side-to-side' => a.type == 'rack' || (ax['sidesOpen'] == false) ? 'it breathes through its sides, which are closed in a ${a.type}' : null,
      'bottom-to-top' => ax['topOpen'] == false ? 'its exhaust at the top is closed in' : null,
      _ => null,
    };
    if (blocked != null) out.add(Finding('heat', 'fail', 'verified', '${p.name}: $blocked; turning it or opening that side helps'));
  }

  // Cables: length against the distance, and room to bend.
  final cables = ax['cables'];
  if (cables is List) {
    final byId = {for (final p in a.placements) p.id: p};
    for (final c in cables.whereType<Map>()) {
      final name = '${c['name'] ?? 'cable'}';
      final from = byId[c['from']], to = byId[c['to']];
      final len = Qty.fromJson(c['length']);
      if (from == null || to == null || len?.base('length') == null) {
        out.add(Finding('cable', 'unknown', 'unknown', '$name: needs from, to and length'));
        continue;
      }
      double? dist;
      if (from.u != null && to.u != null) {
        dist = (from.u!.$1 - to.u!.$1).abs() * _uMm + 150; // up the rails plus about 150 mm to reach round
      } else if (_q(from.extra, 'x') != null && _q(to.extra, 'x') != null && _q(from.extra, 'y') != null && _q(to.extra, 'y') != null) {
        dist = (_q(from.extra, 'x')!.base('length')! - _q(to.extra, 'x')!.base('length')!).abs() + (_q(from.extra, 'y')!.base('length')! - _q(to.extra, 'y')!.base('length')!).abs();
      }
      if (dist == null) {
        out.add(Finding('cable', 'unknown', 'unknown', '$name: the two ends have no positions'));
      } else if (len!.base('length')! < dist) {
        out.add(Finding('cable', 'fail', 'estimated', '$name is ${_n(len.base('length')!)} mm; the route is about ${_n(dist)} mm'));
      }
      final bend = Qty.fromJson(c['minBend'])?.base('length');
      if (bend != null && ad != null) {
        final deepest = math.max(from.depth?.base('length') ?? 0, to.depth?.base('length') ?? 0);
        final room = ad - deepest;
        if (room < bend) out.add(Finding('cable', 'fail', 'verified', '$name needs ${_n(bend)} mm to bend; there is ${_n(room)} mm behind the deepest part'));
      }
    }
  }
  return out;
}
