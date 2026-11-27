import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';

import 'theme.dart';

List<Event> readDeviceActivity(String root, String id, DateTime now) =>
    readEvents(
      root,
      now.subtract(const Duration(days: 7)),
      now,
    ).reversed.where((e) => e.device == id || e.subject == id).toList();

class DeviceActivity extends StatelessWidget {
  const DeviceActivity(this.name, this.events, this.names, {super.key});

  final String name;
  final Future<List<Event>> events;
  final Map<String, String> names;

  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppBar(
      title: Text('$name activity', overflow: TextOverflow.ellipsis),
    ),
    body: FutureBuilder<List<Event>>(
      future: events,
      builder: (context, snapshot) {
        if (snapshot.hasError) {
          return const Center(child: Text('Could not read recorded activity.'));
        }
        final list = snapshot.data;
        if (list == null) {
          return const Center(child: CircularProgressIndicator());
        }
        if (list.isEmpty) {
          return const Center(
            child: Text('No events recorded in the last 7 days.'),
          );
        }
        final locale = MaterialLocalizations.of(context);
        return Center(
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 760),
            child: ListView.separated(
              padding: const EdgeInsets.fromLTRB(16, 12, 16, 24),
              itemCount: list.length,
              separatorBuilder: (_, _) => const Divider(height: 1),
              itemBuilder: (context, index) {
                final event = list[index];
                final local = event.time.toLocal();
                final observer = event.device;
                final subject = event.name ?? names[event.subject];
                return Padding(
                  padding: const EdgeInsets.symmetric(vertical: 12),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        '${locale.formatShortDate(local)} · ${locale.formatTimeOfDay(TimeOfDay.fromDateTime(local))}',
                        style: const TextStyle(color: C.muted, fontSize: 12),
                      ),
                      const SizedBox(height: 4),
                      Text(
                        '${_label(event.type)}${subject == null ? '' : ' · $subject'}',
                        style: const TextStyle(fontWeight: FontWeight.w600),
                      ),
                      if (event.detail case final String detail
                          when detail.isNotEmpty)
                        Text(detail, style: const TextStyle(color: C.muted)),
                      if (observer != null)
                        Text(
                          'Recorded by ${names[observer] ?? observer}',
                          style: const TextStyle(color: C.muted, fontSize: 12),
                        ),
                    ],
                  ),
                );
              },
            ),
          ),
        );
      },
    ),
  );
}

String _label(String type) => switch (type) {
  'peer.offline' => 'Stopped reporting',
  'peer.online' => 'Reporting again',
  'power.battery' => 'Switched to battery',
  'power.mains' => 'Mains power returned',
  'container.stopped' => 'Container stopped',
  'container.started' => 'Container started',
  'container.unhealthy' => 'Container health warning',
  'thing.plugged' => 'Connected',
  'thing.unplugged' => 'Disconnected',
  'app.updated' => 'App updated',
  'control.applied' => 'Settings request handled',
  _ => type,
};
