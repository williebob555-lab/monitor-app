import 'model.dart';
import 'project.dart';
import 'timeline.dart';

/// Model time travel (roadmap row 28): one past moment with both the design
/// (items and projects as they were written then) and the live state
/// ([topologyAt]), joined through each item's `observes`.
class SystemAt {
  SystemAt(this.t, this.live, this.items, this.projects);
  final DateTime t;
  final Moment live;
  final List<Item> items;
  final List<Project> projects;

  /// For an item linked to a device or thing: whether it was up (a device)
  /// or attached and on (a thing) at [t]. Null when it observes nothing
  /// that existed then.
  bool? upAt(Item it) {
    final ids = [if (it.observes != null) it.observes!, if (it.extra['alsoObserves'] is List) for (final x in it.extra['alsoObserves'] as List) '$x'];
    bool? out;
    for (final id in ids) {
      final d = live.devices[id];
      if (d != null) {
        if (d.up) return true;
        out = false;
        continue;
      }
      for (final dev in live.devices.values) {
        if (dev.things.contains(id) || dev.containers[id] == 'running') return true;
        if (dev.containers.containsKey(id)) out = false;
      }
    }
    return out;
  }
}

SystemAt systemAt(String root, DateTime t, {DateTime? now}) => SystemAt(
      t,
      topologyAt(root, t, now: now),
      ModelStore(root, 'reader').asOf(t),
      ProjectStore(root, 'reader').asOf(t),
    );
