import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';
import 'package:monitor_lite/src/past_moment_page.dart';
import 'package:monitor_lite/src/theme.dart';

void main() {
  testWidgets('recorded workspace shows the revisions at the chosen time', (
    tester,
  ) async {
    final root = Directory.systemTemp.createTempSync('monitor-past-projects');
    addTearDown(() => root.deleteSync(recursive: true));
    final at = DateTime.now().subtract(const Duration(hours: 1));
    final inventory = ModelStore(root.path, 'laptop');
    final projects = ProjectStore(root.path, 'laptop');
    final item = inventory.put(
      Item(id: 'amp', type: 'part', name: 'Original amplifier'),
      now: at.subtract(const Duration(minutes: 5)),
    );
    inventory.put(
      item..name = 'New amplifier name',
      now: at.add(const Duration(minutes: 5)),
    );
    final project = projects.put(
      Project(id: 'rack', name: 'Original plan'),
      now: at.subtract(const Duration(minutes: 5)),
    );
    projects.put(
      project..name = 'New plan name',
      now: at.add(const Duration(minutes: 5)),
    );

    await tester.runAsync(() async {
      await tester.pumpWidget(
        MaterialApp(theme: buildTheme(), home: PastMomentPage(root.path, at)),
      );
      for (
        var i = 0;
        i < 100 && find.text('Original plan').evaluate().isEmpty;
        i++
      ) {
        await Future<void>.delayed(const Duration(milliseconds: 20));
        await tester.pump();
      }
    });
    expect(find.text('Original amplifier'), findsOneWidget);
    expect(find.text('Original plan'), findsOneWidget);
    expect(find.text('New amplifier name'), findsNothing);
    expect(find.text('New plan name'), findsNothing);
  });
}
