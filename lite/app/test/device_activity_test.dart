import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';
import 'package:monitor_lite/src/device_activity.dart';
import 'package:monitor_lite/src/theme.dart';

void main() {
  testWidgets('activity keeps event subjects and observers distinct', (
    tester,
  ) async {
    final temp = Directory.systemTemp.createTempSync('monitor-activity-');
    addTearDown(() => temp.deleteSync(recursive: true));
    final folder = SyncFolder(temp.path);
    final now = DateTime.utc(2026, 11, 27, 16);
    appendEvents(folder.deviceDir('server'), [
      Event(
        now.subtract(const Duration(hours: 2)),
        'peer.offline',
        subject: 'phone',
        name: 'Phone',
      ),
      Event(
        now.subtract(const Duration(hours: 1)),
        'peer.online',
        subject: 'phone',
        name: 'Phone',
      ),
      Event(
        now.subtract(const Duration(minutes: 20)),
        'peer.online',
        subject: 'tablet',
        name: 'Tablet',
      ),
    ]);
    appendEvents(folder.deviceDir('phone'), [
      Event(
        now.subtract(const Duration(minutes: 40)),
        'app.updated',
        detail: '2.6.7',
      ),
    ]);
    final activity = readDeviceActivity(temp.path, 'phone', now);
    expect(activity.map((event) => event.type), [
      'app.updated',
      'peer.online',
      'peer.offline',
    ]);

    await tester.binding.setSurfaceSize(const Size(390, 780));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    await tester.pumpWidget(
      MaterialApp(
        theme: buildTheme(),
        home: DeviceActivity('Phone', Future.value(activity), const {
          'server': 'Server',
          'phone': 'Phone',
        }),
      ),
    );
    await tester.pumpAndSettle();
    expect(find.text('App updated'), findsOneWidget);
    expect(find.text('Reporting again · Phone'), findsOneWidget);
    expect(find.text('Recorded by Server'), findsNWidgets(2));
    expect(find.textContaining('Tablet'), findsNothing);
  });
}
