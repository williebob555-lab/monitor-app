import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';
import 'package:monitor_lite/src/inventory_page.dart';
import 'package:monitor_lite/src/inventory_link.dart';
import 'package:monitor_lite/src/theme.dart';

void main() {
  testWidgets('two boot systems add one owned inventory item', (tester) async {
    final root = Directory.systemTemp.createTempSync(
      'monitor-observed-inventory',
    );
    addTearDown(() => root.deleteSync(recursive: true));
    final folder = SyncFolder(root.path);
    final now = DateTime.now();
    folder
      ..prepare('win')
      ..prepare('linux')
      ..writeNow(
        Report(
          id: 'win',
          name: 'Laptop Windows',
          kind: 'windows',
          time: now,
          machine: 'same-laptop',
        ),
      )
      ..writeNow(
        Report(
          id: 'linux',
          name: 'Laptop Linux',
          kind: 'linux',
          time: now.subtract(const Duration(hours: 1)),
          machine: 'same-laptop',
        ),
      );
    await tester.runAsync(() async {
      await tester.pumpWidget(
        MaterialApp(
          theme: buildTheme(),
          home: Scaffold(body: InventoryLink(root.path, 'tablet', 'linux')),
        ),
      );
      for (
        var i = 0;
        i < 100 && find.text('This is mine').evaluate().isEmpty;
        i++
      ) {
        await Future<void>.delayed(const Duration(milliseconds: 20));
        await tester.pump();
      }
      expect(find.text('This is mine'), findsOneWidget);
      await tester.tap(find.text('This is mine'));
      await tester.pumpAndSettle();
      await tester.tap(find.text('Add'));
      for (
        var i = 0;
        i < 100 && ModelStore(root.path, 'tablet').list().isEmpty;
        i++
      ) {
        await Future<void>.delayed(const Duration(milliseconds: 20));
        await tester.pump();
      }
    });
    final items = ModelStore(root.path, 'tablet').list();
    expect(items.single.observes, 'win');
    expect(items.single.state, 'owned');
    expect(inventorySuggestion((root.path, 'tablet', 'linux')), isNull);
  });

  testWidgets(
    'quick capture saves to core storage and edit preserves existing evidence',
    (tester) async {
      final root = Directory.systemTemp.createTempSync('monitor-inventory-');
      addTearDown(() => root.deleteSync(recursive: true));
      final store = ModelStore(root.path, 'desktop');
      final anotherInstall = ModelStore(root.path, 'tablet');
      await tester.binding.setSurfaceSize(const Size(390, 780));
      addTearDown(() => tester.binding.setSurfaceSize(null));
      await tester.pumpWidget(
        MaterialApp(theme: buildTheme(), home: InventoryPage(store)),
      );
      await tester.tap(find.text('Add a thing'));
      await tester.pumpAndSettle();
      await tester.enterText(find.byType(TextFormField), 'Power amp');
      await tester.tap(find.text('Save'));
      await tester.pumpAndSettle();

      final item = anotherInstall.list().single;
      expect(item.name, 'Power amp');
      expect(item.state, 'proposed');
      item.props['weight'] = Prop(7.0, unit: 'kg', src: 'measured');
      item.props['height'] = Prop(null, src: 'unknown');
      anotherInstall.put(item);

      await tester.tap(find.text('Power amp'));
      await tester.pumpAndSettle();
      expect(find.text('7.0 kg · Measured'), findsOneWidget);
      expect(find.text('Not available · Unknown source'), findsOneWidget);
      await tester.enterText(find.byType(TextFormField), 'Stereo power amp');
      await tester.tap(find.text('Save'));
      await tester.pumpAndSettle();
      final saved = anotherInstall.get(item.id)!;
      expect(saved.name, 'Stereo power amp');
      expect(saved.props['weight']?.src, 'measured');
      expect(saved.props['weight']?.value, 7);
    },
  );
}
