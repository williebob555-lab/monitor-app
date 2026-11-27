import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';

import 'theme.dart';
import 'widgets.dart';

Uri? serviceInterface(Report report) {
  final address = report.readings
      .where((r) => r.key == 'ct.url')
      .firstOrNull
      ?.value;
  if (address is! String) return null;
  final uri = Uri.tryParse(address);
  if (uri == null ||
      (uri.scheme != 'http' && uri.scheme != 'https') ||
      uri.host.isEmpty ||
      uri.userInfo.isNotEmpty) {
    return null;
  }
  return uri;
}

class ServicesPanel extends StatelessWidget {
  const ServicesPanel({
    super.key,
    required this.projects,
    required this.containers,
    required this.onInspect,
    required this.onInterface,
  });

  final List<DeviceStatus> projects;
  final List<DeviceStatus> containers;
  final ValueChanged<String> onInspect;
  final ValueChanged<Uri> onInterface;

  @override
  Widget build(BuildContext context) => Section(
    'Services',
    Column(
      crossAxisAlignment: CrossAxisAlignment.stretch,
      children: [
        for (final project in projects) ...[
          if (project != projects.first) const Divider(height: 1),
          ExpansionTile(
            key: PageStorageKey(project.report.id),
            tilePadding: EdgeInsets.zero,
            shape: const Border(),
            collapsedShape: const Border(),
            leading: _ServiceStatus(project.level),
            title: Text(
              project.report.name,
              style: const TextStyle(fontWeight: FontWeight.w600),
            ),
            subtitle: Text(
              project.title,
              style: const TextStyle(color: C.muted),
            ),
            children: [
              for (final service in containers.where(
                (status) => status.report.node?.parent == project.report.id,
              ))
                _ServiceRow(
                  service,
                  onInspect: onInspect,
                  onInterface: onInterface,
                ),
              Align(
                alignment: Alignment.centerLeft,
                child: TextButton(
                  onPressed: () => onInspect(project.report.id),
                  child: const Text('Project details'),
                ),
              ),
            ],
          ),
        ],
      ],
    ),
  );
}

class _ServiceStatus extends StatelessWidget {
  const _ServiceStatus(this.level);
  final Level level;

  @override
  Widget build(BuildContext context) => SizedBox.square(
    dimension: 9,
    child: DecoratedBox(
      decoration: BoxDecoration(
        shape: BoxShape.circle,
        color: level == Level.warn || level == Level.fault
            ? C.of(level)
            : C.inactive,
      ),
    ),
  );
}

class _ServiceRow extends StatelessWidget {
  const _ServiceRow(
    this.status, {
    required this.onInspect,
    required this.onInterface,
  });

  final DeviceStatus status;
  final ValueChanged<String> onInspect;
  final ValueChanged<Uri> onInterface;

  @override
  Widget build(BuildContext context) {
    final report = status.report;
    final health = report.readings
        .where((r) => r.key == 'ct.health')
        .firstOrNull
        ?.value;
    final uri = serviceInterface(report);
    final text =
        health is String && status.fresh && !status.title.contains(health)
        ? '${status.title} · $health'
        : status.title;
    return ListTile(
      dense: true,
      contentPadding: const EdgeInsets.only(left: 12),
      leading: _ServiceStatus(status.level),
      title: Text(report.name),
      subtitle: Text(text),
      onTap: () => onInspect(report.id),
      trailing: uri == null
          ? null
          : IconButton(
              tooltip: 'Open ${report.name} web interface',
              icon: const Icon(Icons.open_in_new, size: 18),
              onPressed: () => onInterface(uri),
            ),
    );
  }
}
