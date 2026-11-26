import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';

/// Dark graphite workstation look. Color only marks status, never decorates.
class C {
  static const bg = Color(0xFF14171B);
  static const surface = Color(0xFF1C2026);
  static const raised = Color(0xFF232830);
  static const line = Color(0xFF2C323B);
  static const text = Color(0xFFE6E9EE);
  static const muted = Color(0xFF8B93A1);
  static const ok = Color(0xFF3FB950);
  static const warn = Color(0xFFD9A62B);
  static const fault = Color(0xFFF85149);
  static const inactive = Color(0xFF6E7681);
  static const flow = Color(0xFF39C5CF);

  static Color of(Level l) => switch (l) {
        Level.ok => ok,
        Level.warn => warn,
        Level.fault => fault,
        Level.inactive => inactive,
      };
}

ThemeData buildTheme() {
  final base = ThemeData(
    brightness: Brightness.dark,
    scaffoldBackgroundColor: C.bg,
    colorScheme: const ColorScheme.dark(
      surface: C.surface,
      primary: C.flow,
      onPrimary: Color(0xFF071417),
      secondary: C.flow,
      onSurface: C.text,
      outline: C.line,
    ),
    dividerColor: C.line,
    useMaterial3: true,
  );
  return base.copyWith(
    appBarTheme: const AppBarTheme(backgroundColor: C.bg, foregroundColor: C.text, elevation: 0, scrolledUnderElevation: 0),
    cardTheme: const CardThemeData(
      color: C.surface,
      elevation: 0,
      margin: EdgeInsets.zero,
      shape: RoundedRectangleBorder(borderRadius: BorderRadius.all(Radius.circular(14)), side: BorderSide(color: C.line)),
    ),
    textTheme: base.textTheme.apply(bodyColor: C.text, displayColor: C.text),
  );
}

String fmtBytes(num? b) {
  if (b == null) return '-';
  const u = ['B', 'KB', 'MB', 'GB', 'TB'];
  var v = b.toDouble();
  var i = 0;
  while (v >= 1000 && i < u.length - 1) {
    v /= 1024;
    i++;
  }
  return '${v >= 100 || i == 0 ? v.round() : v.toStringAsFixed(1)} ${u[i]}';
}

String fmtRate(num? bps) => bps == null ? '-' : '${fmtBytes(bps)}/s';

double? pct(int? used, int? total) => used == null || total == null || total == 0 ? null : 100 * used / total;
