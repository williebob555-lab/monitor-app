import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';
import 'package:monitor_lite/src/request_activity.dart';
import 'package:monitor_lite/src/theme.dart';

void main() {
  testWidgets(
    'shows only this device’s requests and updates when a host answers',
    (tester) async {
      final root = Directory.systemTemp.createTempSync(
        'monitor-requests-widget-',
      );
      addTearDown(() => root.deleteSync(recursive: true));
      await tester.binding.setSurfaceSize(const Size(390, 780));
      addTearDown(() => tester.binding.setSurfaceSize(null));

      final folder = SyncFolder(root.path);
      final now = DateTime.utc(2026, 11, 27);
      ControlRequest request(
        String target,
        Duration age, {
        String from = 'phone',
        String key = 'update',
      }) => ControlRequest(
        id: ControlRequest.newId(),
        from: from,
        fromName: 'Phone',
        host: target,
        target: target,
        key: key,
        value: key == 'update' ? 'update' : 'off',
        time: now.subtract(age),
      );
      final waiting = request('desktop', const Duration(minutes: 2));
      final expired = request('laptop', const Duration(minutes: 11));
      final failed = request(
        'server',
        const Duration(minutes: 3),
        key: 'ups.alarm',
      );
      final other = request(
        'other',
        const Duration(minutes: 1),
        from: 'tablet',
      );
      for (final item in [waiting, expired, failed, other]) {
        folder.sendRequest(item);
      }
      folder.writeResult(
        failed.host,
        ControlResult(
          id: failed.id,
          ok: false,
          message: 'The device refused the request',
          time: now,
        ),
      );

      Future<void> draw() => tester.pumpWidget(
        MaterialApp(
          theme: buildTheme(),
          home: RequestActivity(
            requests: folder.myRequests('phone'),
            answerFor: (r) => folder.resultOf(r.host, r.id),
            names: const {
              'desktop': 'Desktop',
              'laptop': 'Laptop',
              'server': 'Server',
              'other': 'Other',
            },
            now: now,
          ),
        ),
      );
      await draw();
      expect(find.textContaining('Waiting ·'), findsOneWidget);
      expect(find.textContaining('Expired ·'), findsOneWidget);
      expect(find.textContaining('Failed ·'), findsOneWidget);
      expect(find.text('The device refused the request'), findsOneWidget);
      expect(find.text('Update · Other'), findsNothing);

      folder.writeResult(
        waiting.host,
        ControlResult(
          id: waiting.id,
          ok: true,
          message: 'Update notification posted',
          time: now,
        ),
      );
      await draw();
      expect(find.text('Nothing is waiting.'), findsOneWidget);
      expect(find.textContaining('Answered ·'), findsOneWidget);
      expect(find.text('Update notification posted'), findsOneWidget);
    },
  );
}
