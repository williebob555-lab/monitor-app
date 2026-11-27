import 'dart:math' as math;

import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';

import 'theme.dart';
import 'widgets.dart';

class PatternHistoryPage extends StatefulWidget {
  const PatternHistoryPage(
    this.device,
    this.history, {
    super.key,
    this.patterns = const [],
  });

  final String device;
  final Future<List<Map<String, Object?>>> history;
  final List<Pattern> patterns;

  @override
  State<PatternHistoryPage> createState() => _PatternHistoryPageState();
}

class _PatternHistoryPageState extends State<PatternHistoryPage> {
  String? selected;

  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppBar(title: const Text('Pattern history')),
    body: FutureBuilder<List<Map<String, Object?>>>(
      future: widget.history,
      builder: (context, snapshot) {
        if (snapshot.hasError) {
          return const Center(child: Text('Could not read recorded patterns.'));
        }
        final rows = snapshot.data;
        if (rows == null) {
          return const Center(child: CircularProgressIndicator());
        }
        final keys = {
          for (final row in rows)
            for (final entry in row.entries)
              if (entry.key != 't' &&
                  entry.value is num &&
                  (entry.value as num).isFinite)
                entry.key,
        }.toList()..sort();
        if (keys.isEmpty) {
          return const Center(
            child: Text(
              'No daily patterns have been recorded for this device.',
            ),
          );
        }
        final key = selected ?? keys.first;
        final known = widget.patterns
            .where((pattern) => pattern.key == key)
            .firstOrNull;
        final unit =
            known?.unit ??
            switch (key) {
              'ups.load.usual' => '%',
              'ups.runtime.full' => 'min',
              _ => null,
            };
        final estimated = known?.estimated == true || key == 'ups.runtime.full';
        final points = [
          for (final row in rows)
            if (row['t'] case final num timestamp)
              if (chartValue(row, key) case final double value)
                if (timestamp.abs() <= 8640000000000 && value.isFinite)
                  _Point(timestamp.toInt(), value),
        ]..sort((a, b) => a.seconds.compareTo(b.seconds));
        return Center(
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 760),
            child: ListView(
              padding: const EdgeInsets.fromLTRB(16, 12, 16, 28),
              children: [
                Text(
                  widget.device,
                  style: Theme.of(context).textTheme.titleMedium,
                ),
                const SizedBox(height: 6),
                const Text(
                  'Recorded daily summaries derived from device history.',
                  style: TextStyle(color: C.muted),
                ),
                const SizedBox(height: 16),
                DropdownButtonFormField<String>(
                  initialValue: key,
                  isExpanded: true,
                  decoration: const InputDecoration(
                    labelText: 'Pattern',
                    border: OutlineInputBorder(),
                  ),
                  items: [
                    for (final choice in keys)
                      DropdownMenuItem(
                        value: choice,
                        child: Text(
                          _patternName(choice),
                          maxLines: 1,
                          overflow: TextOverflow.ellipsis,
                        ),
                      ),
                  ],
                  onChanged: (value) => setState(() => selected = value),
                ),
                const SizedBox(height: 12),
                if (points.isEmpty)
                  const Text('No dated values are available for this pattern.')
                else ...[
                  _Trend(points, unit: unit),
                  const SizedBox(height: 12),
                  Section(
                    'Latest entries',
                    Column(
                      children: [
                        for (final point in points.reversed.take(7))
                          Padding(
                            padding: const EdgeInsets.symmetric(vertical: 4),
                            child: Row(
                              children: [
                                Expanded(child: Text(point.date)),
                                Text(_value(point.value, unit)),
                              ],
                            ),
                          ),
                      ],
                    ),
                  ),
                ],
                if (estimated)
                  const Padding(
                    padding: EdgeInsets.only(top: 10),
                    child: Text(
                      'This pattern is an estimate.',
                      style: TextStyle(color: C.muted),
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

String _patternName(String key) => switch (key) {
  'battery.charge' => 'Battery charge rate',
  'battery.drain.off' => 'Battery drain with screen off',
  'battery.drain.on' => 'Battery drain with screen on',
  'battery.drain.off.lasts' => 'Estimated battery life with screen off',
  'battery.drain.on.lasts' => 'Estimated battery life with screen on',
  'cpu.steady' => 'Steady CPU usage',
  'cpu.busiest' => 'CPU usage during busiest period',
  'mem.usual' => 'Usual memory usage',
  'temp.cpu.usual' => 'Usual CPU temperature',
  'net.perDay' => 'Network data per day',
  'ups.cuts' => 'Power cuts',
  'ups.load.usual' => 'Usual UPS load',
  'ups.runtime.full' => 'Estimated UPS runtime at full charge',
  'reach.always' => 'Devices always reached',
  _ => key,
};

String _value(double value, String? unit) {
  if (unit == 'B/day') return '${fmtBytes(value)}/day';
  final fixed = value.toStringAsFixed(2);
  final number = fixed.contains('e')
      ? fixed
      : fixed.replaceFirst(RegExp(r'\.?0+$'), '');
  return unit == null ? number : '$number $unit';
}

class _Point {
  const _Point(this.seconds, this.value);

  final int seconds;
  final double value;

  String get date {
    final day = DateTime.fromMillisecondsSinceEpoch(
      seconds * 1000,
      isUtc: true,
    ).toLocal();
    return '${day.year}-${day.month.toString().padLeft(2, '0')}-${day.day.toString().padLeft(2, '0')}';
  }
}

class _Trend extends StatelessWidget {
  const _Trend(this.points, {this.unit});

  final List<_Point> points;
  final String? unit;

  @override
  Widget build(BuildContext context) {
    final values = points.map((point) => point.value);
    final low = values.reduce(math.min);
    final high = values.reduce(math.max);
    final first = points.first.date;
    final last = points.last.date;
    return Section(
      'Recorded trend',
      Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          Text(
            '${points.length} ${points.length == 1 ? 'entry' : 'entries'} · '
            '${unit ?? 'Units unavailable'}',
            style: const TextStyle(color: C.muted),
          ),
          const SizedBox(height: 8),
          Text('High ${_value(high, unit)}'),
          RepaintBoundary(
            child: SizedBox(
              width: double.infinity,
              height: 116,
              child: CustomPaint(painter: _TrendPainter(points, low, high)),
            ),
          ),
          Text('Low ${_value(low, unit)}'),
          const SizedBox(height: 8),
          Text(
            first == last ? first : '$first to $last',
            style: const TextStyle(color: C.muted),
          ),
        ],
      ),
    );
  }
}

class _TrendPainter extends CustomPainter {
  const _TrendPainter(this.points, this.low, this.high);

  final List<_Point> points;
  final double low, high;

  @override
  void paint(Canvas canvas, Size size) {
    const padding = 5.0;
    if (size.width <= 2 * padding) return;
    final ink = Paint()
      ..color = C.flow
      ..strokeWidth = 1.5;
    final span = points.last.seconds - points.first.seconds;
    final range = high - low;
    Offset? previous;
    int? previousTime;
    for (final point in points) {
      final x = span > 0 ? (point.seconds - points.first.seconds) / span : 0.5;
      final y = range > 0 ? (point.value - low) / range : 0.5;
      final at = Offset(
        padding + (size.width - 2 * padding) * x,
        padding + (size.height - 2 * padding) * (1 - y),
      );
      // A day with no recorded value is left as a gap. A daylight-saving
      // transition can put local midnights 25 hours apart.
      if (previous != null && point.seconds - previousTime! <= 25 * 3600) {
        canvas.drawLine(previous, at, ink);
      }
      canvas.drawCircle(at, 2, ink);
      previous = at;
      previousTime = point.seconds;
    }
  }

  @override
  bool shouldRepaint(_TrendPainter oldDelegate) =>
      oldDelegate.points != points ||
      oldDelegate.low != low ||
      oldDelegate.high != high;
}
