import 'dart:math' as math;

import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';

import 'theme.dart';

class StatusDot extends StatelessWidget {
  const StatusDot(this.level, {super.key, this.size = 10});
  final Level level;
  final double size;
  @override
  Widget build(BuildContext context) => Container(
        width: size,
        height: size,
        decoration: BoxDecoration(
          color: C.of(level),
          shape: BoxShape.circle,
          boxShadow: [BoxShadow(color: C.of(level).withValues(alpha: 0.45), blurRadius: size * 0.8)],
        ),
      );
}

class StatusChip extends StatelessWidget {
  const StatusChip(this.level, this.text, {super.key});
  final Level level;
  final String text;
  @override
  Widget build(BuildContext context) => Container(
        padding: const EdgeInsets.symmetric(horizontal: 10, vertical: 4),
        decoration: BoxDecoration(
          color: C.of(level).withValues(alpha: 0.12),
          borderRadius: BorderRadius.circular(20),
          border: Border.all(color: C.of(level).withValues(alpha: 0.4)),
        ),
        child: Row(mainAxisSize: MainAxisSize.min, children: [
          StatusDot(level, size: 8),
          const SizedBox(width: 7),
          Text(text, style: TextStyle(color: C.of(level), fontWeight: FontWeight.w600, fontSize: 13)),
        ]),
      );
}

/// A labelled bar for a percentage. Amber from 85 %, red from 95 %
/// (for battery: amber under 20 %, red under 10 %).
class Meter extends StatelessWidget {
  const Meter(this.label, this.value, {super.key, this.suffix, this.inverse = false, this.note});
  final String label;
  final double? value;
  final String? suffix;
  final bool inverse;
  final String? note;

  Color get _color {
    final v = value ?? 0;
    if (inverse) return v < 10 ? C.fault : (v < 20 ? C.warn : C.ok);
    return v >= 95 ? C.fault : (v >= 85 ? C.warn : C.flow);
  }

  @override
  Widget build(BuildContext context) {
    return Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
      Row(children: [
        Text(label, style: const TextStyle(color: C.muted, fontSize: 12)),
        const Spacer(),
        Text(value == null ? (note ?? 'n/a') : '${value!.round()}%${suffix == null ? '' : '  $suffix'}',
            style: TextStyle(color: value == null ? C.muted : C.text, fontSize: 12, fontFeatures: const [FontFeature.tabularFigures()])),
      ]),
      const SizedBox(height: 5),
      ClipRRect(
        borderRadius: BorderRadius.circular(3),
        child: SizedBox(
          height: 6,
          child: Stack(children: [
            Container(color: C.raised),
            if (value != null)
              FractionallySizedBox(widthFactor: (value! / 100).clamp(0.0, 1.0), child: Container(color: _color)),
          ]),
        ),
      ),
    ]);
  }
}

/// A small line chart of one series. Drawn once per data change.
class Spark extends StatelessWidget {
  const Spark(this.values, {super.key, this.color = C.flow, this.max, this.height = 56});
  final List<double?> values;
  final Color color;
  final double? max;
  final double height;
  @override
  Widget build(BuildContext context) => RepaintBoundary(
        child: SizedBox(height: height, width: double.infinity, child: CustomPaint(painter: _SparkPainter(values, color, max))),
      );
}

class _SparkPainter extends CustomPainter {
  _SparkPainter(this.v, this.color, this.max);
  final List<double?> v;
  final Color color;
  final double? max;

  @override
  void paint(Canvas canvas, Size size) {
    final grid = Paint()
      ..color = C.line
      ..strokeWidth = 1;
    canvas.drawLine(Offset(0, size.height - 0.5), Offset(size.width, size.height - 0.5), grid);
    final pts = v.whereType<double>();
    if (pts.length < 2) return;
    final top = max ?? math.max(pts.reduce(math.max), 1.0);
    final path = Path();
    var started = false;
    for (var i = 0; i < v.length; i++) {
      final y = v[i];
      if (y == null) {
        started = false;
        continue;
      }
      final x = size.width * i / (v.length - 1);
      final yy = size.height - (y / top).clamp(0.0, 1.0) * (size.height - 2) - 1;
      if (!started) {
        path.moveTo(x, yy);
        started = true;
      } else {
        path.lineTo(x, yy);
      }
    }
    canvas.drawPath(
        path,
        Paint()
          ..color = color
          ..strokeWidth = 1.6
          ..style = PaintingStyle.stroke
          ..strokeJoin = StrokeJoin.round);
  }

  @override
  bool shouldRepaint(_SparkPainter o) => o.v != v || o.color != color;
}

class Section extends StatelessWidget {
  const Section(this.title, this.child, {super.key, this.trailing});
  final String title;
  final Widget child;
  final Widget? trailing;
  @override
  Widget build(BuildContext context) => Card(
        child: Padding(
          padding: const EdgeInsets.fromLTRB(16, 14, 16, 16),
          child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
            Row(children: [
              Text(title.toUpperCase(),
                  style: const TextStyle(color: C.muted, fontSize: 11, letterSpacing: 1.2, fontWeight: FontWeight.w600)),
              const Spacer(),
              ?trailing,
            ]),
            const SizedBox(height: 12),
            child,
          ]),
        ),
      );
}

IconData kindIcon(String kind, [String name = '']) => switch (kind) {
      'android' when RegExp(r'tab|SM-X|SM-T|pad', caseSensitive: false).hasMatch(name) => Icons.tablet_android,
      'android' => Icons.smartphone,
      'windows' => Icons.laptop_windows,
      'linux' => Icons.computer,
      _ => Icons.devices_other,
    };

IconData thingIcon(String? type) => switch (type) {
      'ups' => Icons.battery_charging_full,
      'audio-interface' => Icons.graphic_eq,
      'disk' => Icons.storage,
      'compose-project' => Icons.layers_outlined,
      'container' => Icons.inventory_2_outlined,
      _ => Icons.usb,
    };
