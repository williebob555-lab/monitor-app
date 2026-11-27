import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:monitor_core/monitor_core.dart';
import 'package:monitor_lite/src/timeline.dart';

void main() {
  test('recorded topology uses archived peers and attachments from the selected time', () {
    final root = Directory.systemTemp.createTempSync(
      'monitor-recorded-topology',
    );
    addTearDown(() => root.deleteSync(recursive: true));
    final folder = SyncFolder(root.path);
    final at = DateTime.utc(2026, 11, 27, 12);
    final now = at.add(const Duration(hours: 2));
    void write(String dir, String id, Map<String, Object?> line) {
      HistoryWriter(dir, '${root.path}/spool-$id')
        ..add({'t': at.millisecondsSinceEpoch ~/ 1000, ...line}, at)
        ..flush(now);
    }

    write(folder.deviceDir('server'), 'server', {
      'p': {'laptop': 1},
    });
    write(folder.deviceDir('laptop'), 'laptop', {
      'p': {'server': 0},
    });
    write(folder.thingDir('server', 'rack-ups'), 'ups', {'ups.charge': 81});
    final archived = readRecordedMoment((root.path, at, now));
    final topology = recordedTopology(archived, {
      'server': Report(id: 'server', name: 'Server', kind: 'linux', time: now),
      'rack-ups': Report(
        id: 'rack-ups',
        name: 'Rack UPS',
        kind: 'thing',
        time: now,
      ),
      'added-later': Report(
        id: 'added-later',
        name: 'New device',
        kind: 'linux',
        time: now,
      ),
    });
    expect(archived.fromHourly, false);
    expect(topology.devices.map((device) => device.report.id).toSet(), {
      'server',
      'laptop',
    });
    final server = topology.devices.firstWhere(
      (device) => device.report.id == 'server',
    );
    expect(server.report.peers['laptop']?.reachable, true);
    expect(topology.things.single.report.node?.parent, 'server');
    expect(topology.things.single.report.name, 'Rack UPS');
  });
}
