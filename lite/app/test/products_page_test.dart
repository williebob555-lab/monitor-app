import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';
import 'package:monitor_lite/src/products_page.dart';
import 'package:monitor_lite/src/theme.dart';

void main() {
  testWidgets('add an exact product and link it to one owned item', (
    tester,
  ) async {
    final root = Directory.systemTemp.createTempSync('monitor-products');
    addTearDown(() => root.deleteSync(recursive: true));
    final items = ModelStore(root.path, 'laptop');
    final products = ProductStore(root.path, 'laptop');
    items.put(
      Item(
        id: 'interface',
        type: 'audio-interface',
        name: 'Rack interface',
        state: 'owned',
      ),
    );
    await tester.binding.setSurfaceSize(const Size(390, 780));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    await tester.pumpWidget(
      MaterialApp(theme: buildTheme(), home: ProductsPage(products, items)),
    );

    await tester.tap(find.text('Add a product'));
    await tester.pumpAndSettle();
    await tester.enterText(
      find.widgetWithText(TextFormField, 'Product name'),
      'Rack interface model',
    );
    await tester.enterText(
      find.widgetWithText(TextFormField, 'Manufacturer'),
      'Fixture Audio',
    );
    await tester.enterText(
      find.widgetWithText(TextFormField, 'Exact model'),
      'R2',
    );
    await tester.tap(find.text('Save product'));
    await tester.pumpAndSettle();
    expect(find.text('Fixture Audio · R2'), findsOneWidget);
    await tester.tap(find.text('Rack interface model'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Link inventory item'));
    await tester.pumpAndSettle();
    await tester.tap(find.text('Rack interface'));
    await tester.pumpAndSettle();
    final added = ProductStore(
      root.path,
      'tablet',
    ).of(ModelStore(root.path, 'tablet').get('interface')!);
    expect(added?.maker, 'Fixture Audio');
    expect(added?.model, 'R2');
  });
}
