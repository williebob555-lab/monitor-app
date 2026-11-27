import 'package:flutter/foundation.dart';
import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';

import 'theme.dart';
import 'widgets.dart';

ProjectNeeds readProjectNeeds((String?, String?, Project) query) {
  final (root, writer, project) = query;
  final items = root == null || writer == null
      ? const <Item>[]
      : ModelStore(root, writer).list();
  return projectNeeds(project, {
    for (final item in items) item.id: item,
  }, library: Library.load(root));
}

class ProjectNeedsPage extends StatefulWidget {
  const ProjectNeedsPage(this.project, this.store, {super.key});
  final Project project;
  final ProjectStore? store;

  @override
  State<ProjectNeedsPage> createState() => _ProjectNeedsPageState();
}

class _ProjectNeedsPageState extends State<ProjectNeedsPage> {
  late final _needs = compute(readProjectNeeds, (
    widget.store?.root,
    widget.store?.deviceId,
    widget.project,
  ));

  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppBar(title: const Text('Project needs')),
    body: FutureBuilder<ProjectNeeds>(
      future: _needs,
      builder: (context, snapshot) {
        final needs = snapshot.data;
        if (snapshot.hasError) {
          return const Center(child: Text('Could not review this plan.'));
        }
        if (needs == null) {
          return const Center(child: CircularProgressIndicator());
        }
        final connections = [
          ...needs.unconnected,
          for (final notes in needs.connectionProblems.values) ...notes,
        ];
        final needsAny =
            needs.lines.isNotEmpty ||
            connections.isNotEmpty ||
            needs.physical.isNotEmpty ||
            needs.openTasks.isNotEmpty;
        return Center(
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 760),
            child: ListView(
              padding: const EdgeInsets.fromLTRB(14, 8, 14, 26),
              children: [
                Text(
                  widget.project.name,
                  style: Theme.of(context).textTheme.titleLarge,
                ),
                const SizedBox(height: 10),
                if (!needsAny)
                  const Text(
                    'Add equipment, connections, or tasks to see what’s needed.',
                  ),
                for (final (status, heading) in const [
                  ('missing', 'Missing'),
                  ('to-buy', 'To buy'),
                  ('owned', 'On hand'),
                ])
                  if (needs.lines.any((line) => line.status == status))
                    Section(
                      heading,
                      Column(
                        children: [
                          for (final line in needs.lines.where(
                            (line) => line.status == status,
                          ))
                            ListTile(
                              dense: true,
                              contentPadding: EdgeInsets.zero,
                              title: Text('${line.qty} × ${line.name}'),
                              subtitle: line.why == null
                                  ? null
                                  : Text(line.why!),
                            ),
                        ],
                      ),
                    ),
                if (connections.isNotEmpty) _notes('Connections', connections),
                if (needs.physical.isNotEmpty)
                  Section(
                    'Physical checks',
                    Column(
                      children: [
                        for (final finding in needs.physical)
                          ListTile(
                            dense: true,
                            contentPadding: EdgeInsets.zero,
                            title: Text(finding.text),
                            subtitle: Text(
                              '${finding.level == 'fail' ? 'Needs attention' : 'Unknown'} · ${finding.evidence}',
                            ),
                            leading: Icon(
                              finding.level == 'fail'
                                  ? Icons.warning_amber_rounded
                                  : Icons.help_outline,
                              color: finding.level == 'fail' ? C.warn : C.muted,
                            ),
                          ),
                      ],
                    ),
                  ),
                if (needs.openTasks.isNotEmpty)
                  _notes('Open tasks', needs.openTasks),
              ],
            ),
          ),
        );
      },
    ),
  );

  Widget _notes(String title, List<String> notes) => Section(
    title,
    Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        for (final note in notes)
          Padding(padding: const EdgeInsets.only(bottom: 6), child: Text(note)),
      ],
    ),
  );
}
