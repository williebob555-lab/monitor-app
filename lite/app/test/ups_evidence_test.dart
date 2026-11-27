import 'package:flutter_test/flutter_test.dart';
import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';
import 'package:monitor_lite/src/parts.dart';
import 'package:monitor_lite/src/theme.dart';
import 'package:monitor_lite/src/ups_evidence.dart';

void main() {
  testWidgets('UPS distinguishes reported protection from missing load', (
    tester,
  ) async {
    final report = Report(
      id: 'ups',
      name: 'Rack UPS',
      kind: 'thing',
      time: DateTime.now(),
      node: NodeDecl(id: 'ups', type: 'ups'),
      readings: [
        Reading(key: 'ups.source', value: 'UPower on the host', group: 'UPS'),
        Reading(
          key: 'ups.hostAction',
          value: 'host powers off at 20 %',
          src: 'declared',
          group: 'UPS',
        ),
        Reading(
          key: 'ups.identity',
          value: 'USB 0764:0601 on port 1-5',
          note: 'Moving ports changes its ID',
          group: 'UPS',
        ),
        Reading(
          key: 'ups.load',
          value: null,
          note: 'UPower does not report load',
          src: 'unknown',
          group: 'UPS',
        ),
        Reading(
          key: 'ups.runtime',
          value: 12,
          label: 'Runtime',
          unit: 'min',
          from: 'upower-export',
          src: 'estimated',
          group: 'UPS',
        ),
      ],
    );
    await tester.binding.setSurfaceSize(const Size(390, 780));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    await tester.pumpWidget(
      MaterialApp(
        theme: buildTheme(),
        home: Scaffold(body: UpsEvidence(report)),
      ),
    );
    expect(find.text('Read through UPower on the host'), findsOneWidget);
    expect(
      find.text('Low battery policy · host powers off at 20 % (declared)'),
      findsOneWidget,
    );
    expect(find.textContaining('Moving ports changes its ID'), findsOneWidget);
    expect(
      find.text('Load unavailable · UPower does not report load'),
      findsOneWidget,
    );

    final ups = partsOf(report, {}).single;
    expect(
      ups.facts,
      contains(('Runtime', '12 min (estimated) · Host UPower')),
    );
  });
}
