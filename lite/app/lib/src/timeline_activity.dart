import 'dart:isolate';

import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';

import 'device_activity.dart';
import 'theme.dart';

class TimelineActivity extends StatefulWidget {
  const TimelineActivity(
    this.root, {
    super.key,
    required this.now,
    required this.names,
    required this.onSelect,
  });

  final String root;
  final DateTime now;
  final Map<String, String> names;
  final void Function(DateTime) onSelect;

  @override
  State<TimelineActivity> createState() => _TimelineActivityState();
}

class _TimelineActivityState extends State<TimelineActivity> {
  static const window = Duration(days: 1);
  late DateTime _readAt;
  late Future<List<Event>> _events;

  @override
  void initState() {
    super.initState();
    _load();
  }

  @override
  void didUpdateWidget(TimelineActivity oldWidget) {
    super.didUpdateWidget(oldWidget);
    if (widget.root != oldWidget.root ||
        widget.now.difference(_readAt).abs() >= const Duration(minutes: 5)) {
      _load();
    }
  }

  void _load() {
    final root = widget.root;
    final now = _readAt = widget.now;
    _events = Isolate.run(() => readEvents(root, now.subtract(window), now));
  }

  Future<void> _open(List<Event> events) async {
    final selected = await Navigator.of(context).push<Event>(
      MaterialPageRoute(
        builder: (context) => DeviceActivity(
          'Timeline',
          Future.value(events.reversed.toList()),
          widget.names,
          onSelect: (event) => Navigator.of(context).pop(event),
        ),
      ),
    );
    if (mounted && selected != null) widget.onSelect(selected.time);
  }

  @override
  Widget build(BuildContext context) => FutureBuilder<List<Event>>(
    future: _events,
    builder: (context, snapshot) {
      final start = widget.now.subtract(window);
      final events = [
        for (final event in snapshot.data ?? <Event>[])
          if (!event.time.isBefore(start) && !event.time.isAfter(widget.now))
            event,
      ];
      if (events.isEmpty) return const SizedBox.shrink();
      final ticks = {
        for (final event in events)
          (event.time.difference(start).inSeconds / 900).round(),
      };
      return Column(
        children: [
          Padding(
            padding: const EdgeInsets.symmetric(horizontal: 76),
            child: SizedBox(
              height: 4,
              child: Stack(
                children: [
                  for (final tick in ticks)
                    Align(
                      alignment: Alignment(tick / 48 - 1, 0),
                      child: const DecoratedBox(
                        decoration: BoxDecoration(
                          color: C.muted,
                          shape: BoxShape.circle,
                        ),
                        child: SizedBox.square(dimension: 4),
                      ),
                    ),
                ],
              ),
            ),
          ),
          TextButton(
            onPressed: () => _open(events),
            child: Text('Recorded events (${events.length})'),
          ),
        ],
      );
    },
  );
}
