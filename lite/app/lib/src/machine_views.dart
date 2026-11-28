import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';

Machine? machineFor(Assessment assessment, String id) => assessment.machines
    .where((machine) => machine.systems.any((system) => system.report.id == id))
    .firstOrNull;

String systemName(Report report) =>
    report.os ??
    switch (report.kind) {
      'windows' => 'Windows',
      'linux' => 'Linux',
      'android' => 'Android',
      _ => report.name,
    };

class MachineSystems extends StatelessWidget {
  const MachineSystems(
    this.machine,
    this.selected, {
    super.key,
    required this.onOpen,
  });

  final Machine machine;
  final String selected;
  final ValueChanged<String> onOpen;

  @override
  Widget build(BuildContext context) => Card(
    child: Padding(
      padding: const EdgeInsets.fromLTRB(14, 9, 14, 9),
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            '${machine.current.fresh ? 'Running' : 'Last reported'} · ${systemName(machine.current.report)}',
            style: const TextStyle(fontWeight: FontWeight.w600),
          ),
          Wrap(
            spacing: 8,
            children: [
              for (final system in machine.systems)
                if (system.report.id != selected)
                  TextButton(
                    onPressed: () => onOpen(system.report.id),
                    child: Text('View ${systemName(system.report)} report'),
                  ),
            ],
          ),
        ],
      ),
    ),
  );
}
