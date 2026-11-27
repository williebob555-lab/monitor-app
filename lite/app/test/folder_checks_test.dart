import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';
import 'package:monitor_lite/src/folder_checks.dart';
import 'package:monitor_lite/src/theme.dart';

void main() {
  testWidgets(
    'history check reads an actual archive and reports the measured time',
    (tester) async {
      final root = Directory.systemTemp.createTempSync('monitor-health');
      addTearDown(() => root.deleteSync(recursive: true));
      final folder = SyncFolder(root.path);
      final writer = HistoryWriter(
        folder.deviceDir('tablet'),
        '${root.path}/spool',
      );
      final now = DateTime.now();
      writer.add({'t': now.millisecondsSinceEpoch ~/ 1000, 'cpu': 10}, now);
      writer.flush(now);
      await tester.runAsync(() async {
        await tester.pumpWidget(
          MaterialApp(
            theme: buildTheme(),
            home: Scaffold(body: ArchiveCheck(root.path, 'tablet')),
          ),
        );
        await tester.tap(find.text('Check this device’s archive'));
        for (
          var i = 0;
          i < 100 && find.textContaining('Year view read time').evaluate().isEmpty;
          i++
        ) {
          await Future<void>.delayed(const Duration(milliseconds: 20));
          await tester.pump();
        }
      });
      expect(find.text('No archive problems found.'), findsOneWidget);
      expect(find.textContaining('Year view read time'), findsOneWidget);
    },
  );

  testWidgets(
    'access policy distinguishes everyone, named senders, and nobody',
    (tester) async {
      final root = Directory.systemTemp.createTempSync('monitor-access');
      addTearDown(() => root.deleteSync(recursive: true));
      final settings = File('${root.path}/settings.json');
      await tester.pumpWidget(
        MaterialApp(
          theme: buildTheme(),
          home: Scaffold(
            body: ControlsAccess(root.path, const {'laptop': 'Laptop'}),
          ),
        ),
      );
      expect(
        find.text(
          'Requests are allowed from every device in this Monitor folder.',
        ),
        findsOneWidget,
      );
      settings.writeAsStringSync('{"controls":{"allow":["laptop"]}}');
      await tester.tap(find.text('Reload access list'));
      await tester.pumpAndSettle();
      expect(find.text('Laptop'), findsOneWidget);
      settings.writeAsStringSync('{"controls":{"allow":[]}}');
      await tester.tap(find.text('Reload access list'));
      await tester.pumpAndSettle();
      expect(
        find.text('Requests are disabled for every device.'),
        findsOneWidget,
      );
    },
  );
}
