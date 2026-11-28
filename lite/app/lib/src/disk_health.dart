import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';

import 'theme.dart';
import 'widgets.dart';

class DiskHealth extends StatelessWidget {
  const DiskHealth(this.status, {super.key});

  final DeviceStatus status;

  @override
  Widget build(BuildContext context) {
    final readings = {
      for (final reading in status.report.readings) reading.key: reading,
    };
    final health = readings['disk.health'];
    final temp = readings['disk.temp'];
    final wear = readings['disk.wear'];
    final temperature = status.fresh ? temp?.value : null;
    final endurance = status.fresh ? wear?.value : null;
    final label = status.fresh
        ? switch (health?.value) {
            'passed' => 'SMART passed',
            'asleep' => 'Asleep',
            'FAILING' => 'SMART failing',
            _ => health?.note ?? 'Disk health unavailable',
          }
        : status.title;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Wrap(
          spacing: 7,
          crossAxisAlignment: WrapCrossAlignment.center,
          children: [
            StatusDot(
              status.level == Level.ok ? Level.inactive : status.level,
              size: 7,
            ),
            Text(label, style: const TextStyle(color: C.muted, fontSize: 12)),
            if (temperature is num && temperature.isFinite)
              Text(
                '${_number(temperature)} °C',
                style: const TextStyle(color: C.muted, fontSize: 12),
              ),
          ],
        ),
        if (endurance is num && endurance.isFinite && endurance >= 0) ...[
          const SizedBox(height: 5),
          Text(
            '${_number(endurance)}% of rated endurance used',
            style: const TextStyle(color: C.muted, fontSize: 12),
          ),
          const SizedBox(height: 4),
          LinearProgressIndicator(
            value: (endurance / 100).clamp(0.0, 1.0),
            minHeight: 3,
            color: wear?.level == 'warn' || wear?.level == 'fault'
                ? C.warn
                : C.muted,
            backgroundColor: C.line,
          ),
        ],
      ],
    );
  }
}

String _number(num value) => value == value.roundToDouble()
    ? '${value.round()}'
    : value.toStringAsFixed(1);
