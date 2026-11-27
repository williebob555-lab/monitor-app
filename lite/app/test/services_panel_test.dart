import 'package:flutter_test/flutter_test.dart';
import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';
import 'package:monitor_lite/src/services_panel.dart';
import 'package:monitor_lite/src/theme.dart';

void main() {
  testWidgets(
    'groups services by their reported project and keeps links separate from details',
    (tester) async {
      final now = DateTime.utc(2026, 11, 27);
      DeviceStatus status(
        String id,
        String name,
        String type,
        String parent,
        List<Reading> readings,
      ) {
        final report = Report(
          id: id,
          name: name,
          kind: 'thing',
          time: now,
          connected: true,
          node: NodeDecl(id: id, type: type, parent: parent),
          readings: readings,
        );
        return thingStatus(Thing(report, 'server'), now: now);
      }

      final project = status(
        'home',
        'Home services',
        'compose-project',
        'server',
        [Reading(key: 'cp.running', value: '1 of 2')],
      );
      final dns = status('dns', 'DNS filter', 'container', 'home', [
        Reading(key: 'ct.state', value: 'running'),
        Reading(key: 'ct.health', value: 'healthy'),
        Reading(key: 'ct.url', value: 'http://device.example:8080/admin/'),
        Reading(
          key: 'ct.http',
          value: 503,
          label: 'Page answers',
          level: 'warn',
        ),
        Reading(key: 'ct.httpMs', value: 23, unit: 'ms'),
        Reading(key: 'ph.api', value: 'needs its password', src: 'unknown'),
      ]);
      final files = status('files', 'File service', 'container', 'home', [
        Reading(
          key: 'ct.state',
          value: 'stopped',
          label: 'State',
          level: 'warn',
        ),
        Reading(key: 'ct.url', value: 'http://account:secret@device.example/'),
      ]);
      final unrelated = status(
        'elsewhere',
        'Other service',
        'container',
        'another-project',
        [],
      );
      final inspected = <String>[];
      final opened = <Uri>[];
      await tester.binding.setSurfaceSize(const Size(390, 780));
      addTearDown(() => tester.binding.setSurfaceSize(null));
      await tester.pumpWidget(
        MaterialApp(
          theme: buildTheme(),
          home: Scaffold(
            body: ServicesPanel(
              projects: [project],
              containers: [dns, files, unrelated],
              onInspect: inspected.add,
              onInterface: opened.add,
            ),
          ),
        ),
      );
      await tester.tap(find.text('Home services'));
      await tester.pumpAndSettle();
      expect(find.text('running · healthy'), findsOneWidget);
      expect(find.text('HTTP 503 · 23 ms'), findsOneWidget);
      expect(find.text('Page answers: 503'), findsNothing);
      expect(
        find.text('Pi-hole statistics need an app password'),
        findsOneWidget,
      );
      expect(find.text('State: stopped'), findsOneWidget);
      expect(find.text('Other service'), findsNothing);
      expect(find.byTooltip('Open File service web interface'), findsNothing);

      await tester.tap(find.byTooltip('Open DNS filter web interface'));
      expect(opened, [Uri.parse('http://device.example:8080/admin/')]);
      expect(inspected, isEmpty);
      await tester.tap(find.text('File service'));
      expect(inspected, ['files']);
    },
  );
}
