import 'package:flutter/foundation.dart';
import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';

import 'theme.dart';
import 'widgets.dart';

SystemAt _read((String, DateTime, Moment?) query) {
  final (root, at, topology) = query;
  if (topology == null) return systemAt(root, at);
  return SystemAt(
    at,
    topology,
    ModelStore(root, 'reader').asOf(at),
    ProjectStore(root, 'reader').asOf(at),
  );
}

class PastMomentPage extends StatefulWidget {
  const PastMomentPage(this.root, this.at, {super.key, this.topology});
  final String root;
  final DateTime at;
  final Future<Moment>? topology;

  @override
  State<PastMomentPage> createState() => _PastMomentPageState();
}

class _PastMomentPageState extends State<PastMomentPage> {
  late final _moment = _load();

  Future<SystemAt> _load() async {
    final root = widget.root;
    final at = widget.at;
    return compute(_read, (root, at, await widget.topology));
  }

  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppBar(title: const Text('Recorded workspace')),
    body: FutureBuilder<SystemAt>(
      future: _moment,
      builder: (context, snapshot) {
        final moment = snapshot.data;
        if (snapshot.hasError) {
          return const Center(
            child: Text('Could not read this workspace snapshot.'),
          );
        }
        if (moment == null) {
          return const Center(child: CircularProgressIndicator());
        }
        final time = moment.t.toLocal();
        final locale = MaterialLocalizations.of(context);
        return Center(
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 760),
            child: ListView(
              padding: const EdgeInsets.all(14),
              children: [
                Text(
                  '${locale.formatFullDate(time)} · ${locale.formatTimeOfDay(TimeOfDay.fromDateTime(time))}',
                  style: const TextStyle(color: C.muted),
                ),
                const SizedBox(height: 10),
                Section(
                  'Inventory',
                  moment.items.isEmpty
                      ? const Text('No inventory recorded by this time.')
                      : Column(
                          children: [
                            for (final item in moment.items)
                              ListTile(
                                contentPadding: EdgeInsets.zero,
                                title: Text(item.name),
                                subtitle: Text(switch (moment.upAt(item)) {
                                  true => '${item.state} · Recorded active',
                                  false =>
                                    '${item.state} · No nearby live readings',
                                  null => item.state,
                                }),
                              ),
                          ],
                        ),
                ),
                const SizedBox(height: 10),
                Section(
                  'Projects',
                  moment.projects.isEmpty
                      ? const Text('No projects recorded by this time.')
                      : Column(
                          children: [
                            for (final project in moment.projects)
                              ExpansionTile(
                                tilePadding: EdgeInsets.zero,
                                title: Text(project.name),
                                subtitle: project.goal.isEmpty
                                    ? null
                                    : Text(project.goal),
                                children: [
                                  for (final assembly in project.assemblies)
                                    ListTile(
                                      title: Text(assembly.name),
                                      subtitle: Text(
                                        '${assembly.placements.length} placements',
                                      ),
                                    ),
                                  for (final task in project.tasks)
                                    ListTile(
                                      leading: Icon(
                                        task.done
                                            ? Icons.check_circle_outline
                                            : Icons.circle_outlined,
                                      ),
                                      title: Text(task.text),
                                    ),
                                ],
                              ),
                          ],
                        ),
                ),
              ],
            ),
          ),
        );
      },
    ),
  );
}
