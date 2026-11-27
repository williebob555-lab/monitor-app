import 'package:flutter_test/flutter_test.dart';
import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';
import 'package:monitor_lite/src/dive.dart';
import 'package:monitor_lite/src/parts.dart';
import 'package:monitor_lite/src/theme.dart';

void main() {
  testWidgets(
    'the unusual note belongs to the affected part and stays neutral',
    (tester) async {
      final report = Report(
        id: 'laptop',
        name: 'Laptop',
        kind: 'windows',
        time: DateTime.now(),
        cpuPct: 70,
        readings: [
          Reading(key: 'temp.cpu', value: 60, group: 'Sensors'),
          Reading(
            key: 'unusual.cpu',
            value: 'CPU 70 %, usually 8 % at night',
            group: 'Unusual',
            level: 'warn',
          ),
          Reading(
            key: 'unusual.temp.cpu',
            value: 'Temperature above usual',
            group: 'Unusual',
            level: 'warn',
          ),
        ],
      );
      final parts = partsOf(report, {});
      final processor = parts.firstWhere((part) => part.id == 'cpu');
      expect(processor.unusual, ['CPU 70 %, usually 8 % at night']);
      expect(processor.level, Level.ok);
      expect(parts.firstWhere((part) => part.id == 'g:Sensors').unusual, [
        'Temperature above usual',
      ]);
      expect(parts.any((part) => part.id == 'g:Unusual'), false);
      await tester.pumpWidget(
        MaterialApp(
          theme: buildTheme(),
          home: Scaffold(
            body: SizedBox(
              width: 165,
              height: 104,
              child: PartTile(
                processor,
                live: const [],
                onTap: () {},
                showUnusual: true,
              ),
            ),
          ),
        ),
      );
      expect(find.text('Unusual reading'), findsOneWidget);
    },
  );
}
