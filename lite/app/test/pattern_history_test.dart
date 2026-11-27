import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';
import 'package:monitor_lite/src/pattern_history_page.dart';
import 'package:monitor_lite/src/theme.dart';

void main() {
  final today = DateTime(2026, 11, 26, 12);

  Future<void> open(
    WidgetTester tester,
    List<Map<String, Object?>> rows, {
    List<Pattern> metadata = const [],
  }) async {
    await tester.binding.setSurfaceSize(const Size(390, 780));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    await tester.pumpWidget(
      MaterialApp(
        theme: buildTheme(),
        home: PatternHistoryPage(
          'Alpha',
          Future.value(rows),
          patterns: metadata,
        ),
      ),
    );
    await tester.pumpAndSettle();
  }

  testWidgets('plots the archived patterns with recorded dates and units', (
    tester,
  ) async {
    final fixture = Directory.systemTemp.createTempSync(
      'monitor-pattern-widget-',
    );
    addTearDown(() => fixture.deleteSync(recursive: true));
    Pattern cpu(double value) => Pattern(
      key: 'cpu.steady',
      group: 'CPU',
      text: 'Steady CPU usage',
      value: value,
      unit: '%',
      hours: 10,
    );
    const temp = Pattern(
      key: 'temp.cpu.usual',
      group: 'Temperature',
      text: 'Usual CPU temperature',
      value: -2,
      unit: '°C',
      hours: 10,
    );
    recordPatterns(fixture.path, [cpu(0)], DateTime(2026, 11, 24, 12));
    recordPatterns(fixture.path, [cpu(40), temp], today);

    await open(
      tester,
      readPatternHistory(fixture.path, today),
      metadata: [cpu(40), temp],
    );
    expect(find.text('High 40 %'), findsOneWidget);
    expect(find.text('Low 0 %'), findsOneWidget);
    expect(find.text('2026-11-24 to 2026-11-26'), findsOneWidget);
    expect(find.text('2 entries · %'), findsOneWidget);

    await tester.tap(find.byType(DropdownButtonFormField<String>));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Usual CPU temperature').last);
    await tester.pumpAndSettle();
    expect(find.text('Low -2 °C'), findsOneWidget);
    expect(find.text('1 entry · °C'), findsOneWidget);
  });

  testWidgets('explains when a device has no recorded patterns', (
    tester,
  ) async {
    await open(tester, []);
    expect(
      find.text('No daily patterns have been recorded for this device.'),
      findsOneWidget,
    );
  });

  testWidgets('archived container starts have a readable name and rate', (
    tester,
  ) async {
    final fixture = Directory.systemTemp.createTempSync(
      'monitor-container-pattern',
    );
    addTearDown(() => fixture.deleteSync(recursive: true));
    recordPatterns(fixture.path, const [
      Pattern(
        key: 'ct.restarts',
        group: 'Container',
        text: 'Started 7 times in 7 days',
        value: 1,
        unit: '/day',
        hours: 168,
      ),
    ], today);
    await open(tester, readPatternHistory(fixture.path, today));
    expect(find.text('Container starts per day'), findsOneWidget);
    expect(find.text('High 1 /day'), findsOneWidget);
  });

  testWidgets(
    'keeps archived UPS runtime labelled as estimated without recent metadata',
    (tester) async {
      final fixture = Directory.systemTemp.createTempSync(
        'monitor-ups-pattern-widget-',
      );
      addTearDown(() => fixture.deleteSync(recursive: true));
      recordPatterns(fixture.path, const [
        Pattern(
          key: 'ups.runtime.full',
          group: 'Power',
          text: 'Expected runtime',
          hours: 5,
          value: 42,
          unit: 'min',
          estimated: true,
        ),
      ], today);

      await open(tester, readPatternHistory(fixture.path, today));
      expect(find.text('Estimated UPS runtime at full charge'), findsOneWidget);
      expect(find.text('High 42 min'), findsOneWidget);
      expect(find.text('This pattern is an estimate.'), findsOneWidget);
    },
  );
}
