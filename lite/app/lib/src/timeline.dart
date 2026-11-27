import 'package:monitor_core/monitor_core.dart';

Moment readRecordedMoment((String, DateTime, DateTime) query) {
  final (root, at, now) = query;
  return topologyAt(
    root,
    at,
    now: now,
    detailDays: Retention.load(root).detailDays,
  );
}

({List<DeviceStatus> devices, List<DeviceStatus> things}) recordedTopology(
  Moment moment,
  Map<String, Report> current, {
  Set<String> hidden = const {},
}) {
  final devices = <DeviceStatus>[];
  final things = <DeviceStatus>[];
  for (final record in moment.devices.values) {
    final id = record.id;
    if (hidden.contains(id)) continue;
    final peer = current[id];
    devices.add(
      DeviceStatus(
        Report(
          id: id,
          name: peer?.name ?? id,
          kind: peer?.kind ?? 'computer',
          time: record.at ?? moment.t,
          peers: {
            for (final entry in record.reach.entries)
              if (!hidden.contains(entry.key))
                entry.key: PeerView(tcp: entry.value ? 'open' : 'timeout'),
          },
        ),
        record.up ? Level.ok : Level.inactive,
        record.up ? 'Recorded' : 'No nearby readings',
        record.up
            ? 'Recorded from its saved history.'
            : 'Its history has no readings near this time.',
        fresh: record.up,
        age: Duration.zero,
      ),
    );
    for (final thing in record.things) {
      if (hidden.contains(thing)) continue;
      things.add(_recordedThing(thing, id, moment.t, current[thing]));
    }
    for (final entry in record.containers.entries) {
      if (hidden.contains(entry.key)) continue;
      things.add(
        _recordedThing(
          entry.key,
          id,
          moment.t,
          current[entry.key],
          containerState: entry.value,
        ),
      );
    }
  }
  return (devices: devices, things: things);
}

DeviceStatus _recordedThing(
  String id,
  String host,
  DateTime at,
  Report? hint, {
  String? containerState,
}) {
  final observed = containerState == null || containerState == 'running';
  return DeviceStatus(
    Report(
      id: id,
      name: hint?.name ?? id,
      kind: 'thing',
      time: at,
      node: NodeDecl(
        id: id,
        type: containerState == null
            ? hint?.node?.type ?? 'thing'
            : 'container',
        parent: host,
      ),
    ),
    observed ? Level.ok : Level.inactive,
    switch (containerState) {
      'running' => 'Recorded running',
      null => 'Recorded attached',
      _ => 'No running record',
    },
    'Recorded under $host.',
    fresh: observed,
    age: Duration.zero,
  );
}
