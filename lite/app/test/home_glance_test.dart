import 'package:flutter_test/flutter_test.dart';
import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';
import 'package:monitor_lite/src/disk_health.dart';
import 'package:monitor_lite/src/home_glance.dart';
import 'package:monitor_lite/src/theme.dart';

void main() {
  testWidgets(
    'disk wear distinguishes a measured zero from an unavailable reading',
    (tester) async {
      final now = DateTime.now();
      DeviceStatus disk(Reading wear) => thingStatus(
        Thing(
          Report(
            id: 'disk',
            name: 'Disk',
            kind: 'thing',
            time: now,
            node: NodeDecl(id: 'disk', type: 'disk'),
            readings: [
              Reading(key: 'disk.health', value: 'passed'),
              Reading(key: 'disk.temp', value: 46),
              wear,
            ],
          ),
          'host',
        ),
        now: now,
      );
      Future<void> show(Reading wear) => tester.pumpWidget(
        MaterialApp(
          theme: buildTheme(),
          home: Scaffold(body: DiskHealth(disk(wear))),
        ),
      );
      await show(Reading(key: 'disk.wear', value: 0));
      expect(find.text('46 °C'), findsOneWidget);
      expect(find.text('0% of rated endurance used'), findsOneWidget);
      expect(
        tester
            .widget<LinearProgressIndicator>(
              find.byType(LinearProgressIndicator),
            )
            .value,
        0,
      );
      await show(Reading(key: 'disk.wear', value: null, src: 'unknown'));
      expect(find.byType(LinearProgressIndicator), findsNothing);
    },
  );

  testWidgets(
    'Home shows the UPS before disk warnings and opens it in one tap',
    (tester) async {
      final now = DateTime.now();
      Thing thing(
        String id,
        String type,
        List<Reading> readings, {
        DateTime? time,
      }) => Thing(
        Report(
          id: id,
          name: id,
          kind: 'thing',
          time: time ?? now,
          node: NodeDecl(id: id, type: type),
          readings: readings,
        ),
        'host',
      );
      final things = {
        'Disk 1': thing('Disk 1', 'disk', [
          Reading(
            key: 'disk.health',
            label: 'Health',
            value: 'failing',
            level: 'fault',
          ),
        ]),
        'UPS': thing('UPS', 'ups', [
          Reading(key: 'ups.status', value: 'battery'),
          Reading(key: 'ups.charge', value: 64),
          Reading(key: 'ups.runtime', value: 0, src: 'estimated'),
        ]),
        'Old disk': thing('Old disk', 'disk', [
          Reading(
            key: 'disk.health',
            label: 'Health',
            value: 'failing',
            level: 'fault',
          ),
        ], time: now.subtract(const Duration(days: 1))),
      };
      String? opened;
      await tester.binding.setSurfaceSize(const Size(390, 780));
      addTearDown(() => tester.binding.setSurfaceSize(null));
      await tester.pumpWidget(
        MaterialApp(
          theme: buildTheme(),
          home: Scaffold(
            body: HomeGlance(things, now: now, onOpen: (id) => opened = id),
          ),
        ),
      );
      final ups = find.text('On battery, 64 %, 0 min left (estimated runtime)');
      final disk = find.textContaining('Disk 1:');
      expect(ups, findsOneWidget);
      expect(tester.getTopLeft(ups).dy, lessThan(tester.getTopLeft(disk).dy));
      expect(find.textContaining('Old disk'), findsNothing);
      await tester.tap(ups);
      expect(opened, 'UPS');
    },
  );
}
