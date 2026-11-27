import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';

import 'theme.dart';
import 'widgets.dart';

class FleetDeviceUpdate {
  const FleetDeviceUpdate(this.device, this.target, this.last, this.now);

  final DeviceStatus device;
  final String? target;
  final (ControlRequest, ControlResult?)? last;
  final DateTime now;

  Report get report => device.report;

  // A result describes the request. The device report proves the version.
  String? get version =>
      report.app ??
      report.controls.where((c) => c.key == 'update').firstOrNull?.value;

  bool get current =>
      device.fresh &&
      version != null &&
      target != null &&
      compareVersions(version!, target!) >= 0;

  bool get _waiting =>
      last != null &&
      last!.$2 == null &&
      now.difference(last!.$1.time).abs() <= ControlRequest.maxAge;

  bool get _recentAnswer {
    final result = last?.$2;
    return result != null &&
        result.ok &&
        now.difference(result.time).abs() <= ControlRequest.maxAge;
  }

  bool get _canAsk => report.controls.any(
    (c) => c.key == 'update' && c.options.contains('update'),
  );

  bool get canSend =>
      _canAsk &&
      device.fresh &&
      device.level != Level.inactive &&
      !current &&
      !_waiting &&
      !_recentAnswer;

  (String, Level) get progress {
    if (current) return ('Version confirmed', Level.ok);
    if (_waiting) return ('Waiting for device', Level.warn);
    if (last case (_, final ControlResult answer)) {
      if (!answer.ok) return ('Request failed', Level.fault);
      if (report.kind == 'android') {
        return ('Check the install prompt', Level.warn);
      }
      return ('Waiting for a version report', Level.warn);
    }
    if (last != null) return ('Request expired', Level.warn);
    if (!device.fresh || device.level == Level.inactive) {
      return (device.title, device.level);
    }
    if (!_canAsk) return ('Updates on the device', Level.inactive);
    return (
      target == null ? 'Version reported' : 'Update available',
      Level.inactive,
    );
  }
}

class FleetUpdates extends StatelessWidget {
  const FleetUpdates(this.rows, {super.key, required this.onRequest});

  final List<FleetDeviceUpdate> rows;
  final ValueChanged<List<FleetDeviceUpdate>> onRequest;

  @override
  Widget build(BuildContext context) {
    final eligible = rows.where((row) => row.canSend).toList();
    final target = rows.firstOrNull?.target;
    final seen = rows
        .where((row) => row.device.fresh && row.version != null)
        .length;
    final current = rows.where((row) => row.current).length;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        const Divider(height: 28, color: C.line),
        Text('Fleet updates', style: Theme.of(context).textTheme.titleMedium),
        if (target != null)
          Text(
            '$current of $seen recently reporting devices on $target or newer',
            style: const TextStyle(color: C.muted),
          ),
        const SizedBox(height: 6),
        for (final row in rows) _FleetRow(row),
        if (eligible.isNotEmpty) ...[
          const SizedBox(height: 12),
          OutlinedButton.icon(
            onPressed: () => onRequest(eligible),
            icon: const Icon(Icons.system_update_alt, size: 18),
            label: Text(
              eligible.length == 1
                  ? 'Request update'
                  : 'Request updates for ${eligible.length} devices',
            ),
          ),
        ],
      ],
    );
  }
}

class _FleetRow extends StatelessWidget {
  const _FleetRow(this.row);
  final FleetDeviceUpdate row;

  @override
  Widget build(BuildContext context) {
    final (status, level) = row.progress;
    final answer = row.last?.$2;
    return DecoratedBox(
      decoration: const BoxDecoration(
        border: Border(bottom: BorderSide(color: C.line)),
      ),
      child: Padding(
        padding: const EdgeInsets.symmetric(vertical: 10),
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Icon(
              kindIcon(row.report.kind, row.report.name),
              color: C.muted,
              size: 19,
            ),
            const SizedBox(width: 10),
            Expanded(
              child: Column(
                crossAxisAlignment: CrossAxisAlignment.start,
                children: [
                  Text(
                    row.report.name,
                    style: const TextStyle(fontWeight: FontWeight.w600),
                  ),
                  Text(
                    row.version == null
                        ? 'Version unavailable'
                        : '${row.device.fresh ? 'Version' : 'Last reported version'} ${row.version}',
                    style: const TextStyle(color: C.muted, fontSize: 12),
                  ),
                  if (!row.current &&
                      answer != null &&
                      answer.message.isNotEmpty)
                    Text(
                      answer.message,
                      style: const TextStyle(color: C.muted, fontSize: 12),
                    ),
                ],
              ),
            ),
            const SizedBox(width: 10),
            Flexible(
              child: Text(
                status,
                textAlign: TextAlign.end,
                style: TextStyle(
                  color: level == Level.ok || level == Level.inactive
                      ? C.muted
                      : C.of(level),
                  fontSize: 12,
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }
}
