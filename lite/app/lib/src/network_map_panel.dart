import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';

import 'map_view.dart';
import 'theme.dart';
import 'timeline.dart';

class MapPanel extends StatefulWidget {
  const MapPanel({
    super.key,
    required this.folder,
    required this.devices,
    required this.things,
    required this.myId,
    required this.openLive,
    this.now,
  });

  final SyncFolder folder;
  final List<DeviceStatus> devices;
  final List<DeviceStatus> things;
  final String myId;
  final ValueChanged<String> openLive;
  final DateTime Function()? now;

  @override
  State<MapPanel> createState() => _MapPanelState();
}

class _MapPanelState extends State<MapPanel> {
  late Timeline timeline = Timeline(widget.folder);
  DateTime? selectedTime;

  static const minutesPerDay = 24 * 60;

  @override
  void didUpdateWidget(MapPanel oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (widget.folder != oldWidget.folder) {
      timeline = Timeline(widget.folder);
      selectedTime = null;
    }
  }

  @override
  Widget build(BuildContext context) {
    final now = widget.now?.call() ?? DateTime.now();
    final at = selectedTime;
    final devices = widget.devices;
    if (at != null) timeline.load(devices.map((d) => d.report.id), now);
    final snapshot = at == null
        ? devices
        : timeline.at(devices.map((d) => d.report).toList(), at);
    final minutesBack = (at == null ? 0 : now.difference(at).inMinutes).clamp(
      0,
      minutesPerDay,
    );
    return Column(
      children: [
        if (at != null)
          Padding(
            padding: const EdgeInsets.only(bottom: 6),
            child: Text(
              'Recorded · ${_timestamp(context, at)}',
              style: const TextStyle(color: C.warn),
            ),
          ),
        Expanded(
          child: NetworkMap(
            snapshot,
            key: ValueKey(at == null),
            myId: widget.myId,
            things: at == null ? widget.things : const [],
            recorded: at != null,
            onOpen: (id) {
              if (at == null) {
                widget.openLive(id);
              } else {
                final device = snapshot.firstWhere((d) => d.report.id == id);
                _openRecordedDevice(context, device, at);
              }
            },
          ),
        ),
        Row(
          children: [
            const Text(
              '24 h ago',
              style: TextStyle(color: C.muted, fontSize: 11.5),
            ),
            Expanded(
              child: Slider(
                value: (minutesPerDay - minutesBack).toDouble(),
                min: 0,
                max: minutesPerDay.toDouble(),
                divisions: 96,
                semanticFormatterCallback: (value) => value == minutesPerDay
                    ? 'Live'
                    : _timestamp(
                        context,
                        now.subtract(
                          Duration(minutes: minutesPerDay - value.round()),
                        ),
                      ),
                onChanged: (value) => setState(() {
                  final back = minutesPerDay - value.round();
                  selectedTime = back == 0
                      ? null
                      : now.subtract(Duration(minutes: back));
                }),
              ),
            ),
            TextButton(
              onPressed: at == null
                  ? null
                  : () => setState(() => selectedTime = null),
              child: Text(at == null ? 'Live' : 'Go live'),
            ),
          ],
        ),
      ],
    );
  }

  void _openRecordedDevice(
    BuildContext context,
    DeviceStatus device,
    DateTime at,
  ) {
    final report = device.report;
    final battery = report.battery?.pct;
    final cpu = report.cpuPct;
    showDialog<void>(
      context: context,
      builder: (context) => AlertDialog(
        title: Text(report.name),
        scrollable: true,
        content: Column(
          mainAxisSize: MainAxisSize.min,
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Text(
              'Recorded · ${_timestamp(context, at)}',
              style: const TextStyle(color: C.warn),
            ),
            const SizedBox(height: 8),
            if (device.fresh) ...[
              Text('Sampled ${_timestamp(context, report.time)}'),
              Text(device.title),
              if (battery != null) Text('Battery ${battery.round()}%'),
              if (cpu != null) Text('CPU ${cpu.round()}%'),
            ] else
              Text(device.detail),
          ],
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context),
            child: const Text('Close'),
          ),
        ],
      ),
    );
  }
}

String _timestamp(BuildContext context, DateTime instant) {
  final local = instant.toLocal();
  final locale = MaterialLocalizations.of(context);
  return '${locale.formatShortDate(local)} ${locale.formatTimeOfDay(TimeOfDay.fromDateTime(local))}';
}
