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

String? serviceVisibility(DeviceStatus host) {
  if (!host.fresh) return null;
  final access = host.report.readings
      .where((reading) => reading.key == 'ct.access')
      .firstOrNull;
  if (access?.value != 'not visible') return null;
  final note = access?.note?.trim();
  return note == null || note.isEmpty
      ? 'The collector cannot see this host’s containers.'
      : note;
}

class ServicesUnavailable extends StatelessWidget {
  const ServicesUnavailable(this.reason, {super.key});
  final String reason;

  @override
  Widget build(BuildContext context) => Section(
    'Services',
    Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        const Text('Containers aren’t visible'),
        Text(reason, style: const TextStyle(color: C.muted)),
      ],
    ),
  );
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
    final uri = serviceInterface(report);
    Reading? reading(String key) =>
        report.readings.where((r) => r.key == key).firstOrNull;
    final health = reading('ct.health')?.value;
    final http = status.fresh ? reading('ct.http') : null;
    final latency = reading('ct.httpMs')?.value;
    final reportedState =
        http != null &&
            status.title == '${http.label ?? http.key}: ${http.value}'
        ? reading('ct.state')?.value?.toString() ?? 'State unavailable'
        : status.title;
    final httpText = switch (http?.value) {
      final int code => 'HTTP $code${latency is num ? ' · $latency ms' : ''}',
      'no answer' => 'HTTP · No answer',
      _ => null,
    };
    final text = [
      reportedState,
      if (health is String && status.fresh && !reportedState.contains(health))
        health,
    ].join(' · ');
    final piHoleNeedsPassword =
        status.fresh && reading('ph.api')?.value == 'needs its password';
    return ListTile(
      dense: true,
      contentPadding: const EdgeInsets.only(left: 12),
      leading: _ServiceStatus(status.level),
      title: Text(report.name),
      subtitle: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(text),
          if (httpText != null)
            Text(
              httpText,
              style: TextStyle(
                color: switch (http?.level) {
                  'fault' => C.of(Level.fault),
                  'warn' => C.of(Level.warn),
                  _ => C.muted,
                },
              ),
            ),
          if (piHoleNeedsPassword)
            const Text(
              'Pi-hole statistics need an app password',
              style: TextStyle(color: C.muted),
            ),
        ],
      ),
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
