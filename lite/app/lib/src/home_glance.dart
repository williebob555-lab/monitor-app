import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';

import 'theme.dart';

class HomeGlance extends StatelessWidget {
  const HomeGlance(this.things, {super.key, required this.onOpen, this.now});

  final Map<String, Thing> things;
  final ValueChanged<String> onOpen;
  final DateTime? now;

  @override
  Widget build(BuildContext context) {
    final at = now ?? DateTime.now();
    final rows = glance({
      for (final entry in things.entries)
        if (entry.value.report.node?.type != 'disk' ||
            thingStatus(entry.value, now: at).fresh)
          entry.key: entry.value,
    }, now: at);
    if (rows.isEmpty) return const SizedBox.shrink();
    return Card(
      margin: const EdgeInsets.only(bottom: 10),
      child: Column(
        children: [
          for (final row in rows) ...[
            if (row != rows.first) const Divider(height: 1),
            ListTile(
              leading: ExcludeSemantics(
                child: Icon(
                  row.label.startsWith('UPS') ? Icons.power : Icons.storage,
                  color:
                      things[row.thingId]?.report.connected != false &&
                          (row.level == Level.warn || row.level == Level.fault)
                      ? C.of(row.level)
                      : C.muted,
                ),
              ),
              title: Text(
                row.label,
                style: const TextStyle(fontWeight: FontWeight.w600),
              ),
              subtitle: Text(_status(row)),
              trailing: const Icon(Icons.chevron_right, size: 18),
              onTap: () => onOpen(row.thingId),
            ),
          ],
        ],
      ),
    );
  }

  String _status(GlanceLine row) {
    final report = things[row.thingId]?.report;
    if (report?.connected == false) return 'Disconnected';
    final runtime = report?.readings
        .where((reading) => reading.key == 'ups.runtime')
        .firstOrNull;
    final estimated =
        row.level != Level.inactive &&
        runtime?.src == 'estimated' &&
        runtime?.value != null;
    return estimated ? '${row.text} (estimated runtime)' : row.text;
  }
}
