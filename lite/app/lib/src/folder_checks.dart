import 'package:flutter/foundation.dart';
import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';

import 'theme.dart';
import 'widgets.dart';

HistoryIntegrity _checkArchive((String, String, DateTime) request) {
  final (root, id, now) = request;
  final folder = SyncFolder(root);
  return checkHistory(folder.deviceDir(id), now, Retention.load(root));
}

class ArchiveCheck extends StatefulWidget {
  const ArchiveCheck(this.root, this.deviceId, {super.key});
  final String root;
  final String deviceId;

  @override
  State<ArchiveCheck> createState() => _ArchiveCheckState();
}

class _ArchiveCheckState extends State<ArchiveCheck> {
  Future<HistoryIntegrity>? _result;

  void _check() {
    final root = widget.root;
    final id = widget.deviceId;
    final now = DateTime.now();
    setState(() {
      _result = compute(_checkArchive, (root, id, now));
    });
  }

  @override
  Widget build(BuildContext context) => FutureBuilder(
    future: _result,
    builder: (context, snapshot) {
      final running = snapshot.connectionState == ConnectionState.waiting;
      final result = snapshot.data;
      return Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          OutlinedButton(
            onPressed: running ? null : _check,
            child: Text(
              running ? 'Checking archive…' : 'Check this device’s archive',
            ),
          ),
          if (snapshot.hasError)
            const Text(
              'Could not check this device’s archive.',
              style: TextStyle(color: C.warn),
            )
          else if (result != null && !running) ...[
            if (result.problems.isNotEmpty)
              for (final problem in result.problems)
                Text(problem, style: const TextStyle(color: C.warn))
            else if (result.bytes == 0)
              const Text('No archive data on this device yet.')
            else
              const Text('No archive problems found.'),
            if (result.readMs['year'] case final int duration
                when result.bytes > 0)
              Text(
                'Year view read time · $duration ms',
                style: const TextStyle(color: C.muted),
              ),
          ],
        ],
      );
    },
  );
}

class ControlsAccess extends StatefulWidget {
  const ControlsAccess(this.root, this.names, {super.key});
  final String root;
  final Map<String, String> names;

  @override
  State<ControlsAccess> createState() => _ControlsAccessState();
}

class _ControlsAccessState extends State<ControlsAccess> {
  late var _allowed = controlsAllowed(widget.root);

  @override
  Widget build(BuildContext context) {
    final names = [
      for (final id in _allowed ?? <String>{}) widget.names[id] ?? id,
    ]..sort();
    return Section(
      'Allowed to change settings',
      Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          if (_allowed == null)
            const Text(
              'Requests are allowed from every device in this Monitor folder.',
            )
          else if (names.isEmpty)
            const Text('Requests are disabled for every device.')
          else
            for (final name in names)
              ListTile(
                contentPadding: EdgeInsets.zero,
                dense: true,
                title: Text(name),
              ),
          TextButton(
            onPressed: () =>
                setState(() => _allowed = controlsAllowed(widget.root)),
            child: const Text('Reload access list'),
          ),
        ],
      ),
    );
  }
}
