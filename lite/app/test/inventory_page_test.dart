import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';
import 'package:monitor_lite/src/inventory_page.dart';
import 'package:monitor_lite/src/theme.dart';

void main() {
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
      anotherInstall.put(item);

      await tester.tap(find.text('Power amp'));
      await tester.pumpAndSettle();
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
