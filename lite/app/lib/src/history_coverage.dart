import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';

import 'theme.dart';

/// A screen-width projection of archive data returned by monitor_core.
/// A missing bucket says only that there is no archived sample in that slot.
class HistoryCoverage {
  factory HistoryCoverage(
    HistoryRange range,
    DateTime end,
    List<Map<String, Object?>> archive,
  ) {
    final (period, interval) = switch (range) {
      HistoryRange.day => (const Duration(days: 1), const Duration(minutes: 5)),
      HistoryRange.week => (const Duration(days: 7), const Duration(hours: 1)),
      HistoryRange.year => (const Duration(days: 365), const Duration(days: 1)),
    };
    return HistoryCoverage._(end, period, interval, archive);
  }

  HistoryCoverage._(
    this.end,
    this.period,
    this.interval,
    List<Map<String, Object?>> archive,
  ) {
    final until = end.millisecondsSinceEpoch ~/ 1000;
    final first = until - period.inSeconds;
    occupied = List.filled(period.inSeconds ~/ interval.inSeconds, false);
    for (final record in archive) {
      final stamp = record['t'];
      if (stamp is! num || !stamp.isFinite || stamp < first || stamp > until) {
        continue;
      }
      final column = ((stamp.toInt() - first) ~/ interval.inSeconds).clamp(
        0,
        occupied.length - 1,
      );
      records.add((column, record));
      occupied[column] = true;
    }
  }

  final DateTime end;
  final Duration period;
  final Duration interval;
  late final List<bool> occupied;
  final records = <(int, Map<String, Object?>)>[];

  List<double?> values(String key) => [
    for (final (_, row) in records) chartValue(row, key),
  ];

  List<double?> plot(String key) {
    final values = List<double?>.filled(occupied.length, null);
    // Core returns files in timestamp order. Keep the newest known value in
    // each slot while the chart's summary still uses every recorded value.
    for (final (column, row) in records) {
      final value = chartValue(row, key);
      if (value != null && value.isFinite) values[column] = value;
    }
    return values;
  }
}

class CoverageBand extends StatelessWidget {
  const CoverageBand(this.coverage, {super.key});

  final HistoryCoverage coverage;

  @override
  Widget build(BuildContext context) {
    final withData = coverage.occupied.where((value) => value).length;
    final resolution = switch (coverage.interval.inMinutes) {
      5 => '5-minute',
      60 => 'hourly',
      _ => 'daily',
    };
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Row(
          children: [
            const Expanded(
              child: Text(
                'Archive coverage',
                style: TextStyle(fontSize: 12, fontWeight: FontWeight.w600),
              ),
            ),
            Text(
              '$withData of ${coverage.occupied.length} slots',
              style: const TextStyle(fontSize: 11, color: C.muted),
            ),
          ],
        ),
        const SizedBox(height: 6),
        Semantics(
          label:
              '$withData of ${coverage.occupied.length} $resolution slots contain records',
          child: SizedBox(
            height: 8,
            width: double.infinity,
            child: CustomPaint(painter: _CoveragePainter(coverage.occupied)),
          ),
        ),
        const SizedBox(height: 6),
        Text(
          '$resolution slots · dark sections have no archived sample',
          style: const TextStyle(fontSize: 11, color: C.muted),
        ),
      ],
    );
  }
}

class _CoveragePainter extends CustomPainter {
  const _CoveragePainter(this.occupied);
  final List<bool> occupied;

  @override
  void paint(Canvas canvas, Size size) {
    final bar = Rect.fromLTWH(0, 0, size.width, size.height);
    canvas.clipRRect(RRect.fromRectAndRadius(bar, const Radius.circular(3)));
    canvas.drawRect(bar, Paint()..color = C.line);
    final record = Paint()..color = const Color(0xFF8AADB3);
    final width = size.width / occupied.length;
    for (var index = 0; index < occupied.length; index++) {
      if (occupied[index]) {
        canvas.drawRect(
          Rect.fromLTRB(index * width, 0, (index + 1) * width, size.height),
          record,
        );
      }
    }
  }

  @override
  bool shouldRepaint(_CoveragePainter oldDelegate) =>
      oldDelegate.occupied != occupied;
}
