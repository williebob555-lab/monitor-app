import 'package:flutter_test/flutter_test.dart';
import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';
import 'package:monitor_lite/src/connections_page.dart';
import 'package:monitor_lite/src/theme.dart';

void main() {
  testWidgets(
    'search selects connectors and the workbench reports a real mismatch',
    (tester) async {
      await tester.binding.setSurfaceSize(const Size(390, 780));
      addTearDown(() => tester.binding.setSurfaceSize(null));
      await tester.pumpWidget(
        MaterialApp(
          theme: buildTheme(),
          home: Scaffold(body: ConnectionCheck(Library.load(null))),
        ),
      );

      Future<void> choose(Finder button, String query, String connector) async {
        await tester.tap(button);
        await tester.pumpAndSettle();
        await tester.enterText(find.byType(TextField), query);
        await tester.pumpAndSettle();
        await tester.tap(find.text(connector));
        await tester.pumpAndSettle();
      }

      await choose(find.text('Choose connector').first, 'usb-c', 'USB Type-C');
      await choose(find.text('Choose connector'), 'hdmi-a', 'HDMI Type A');
      await tester.tap(find.text('Check connection'));
      await tester.pumpAndSettle();
      expect(find.text('PHYSICAL FIT'), findsOneWidget);
      expect(find.text('Mismatch · verified'), findsWidgets);
      expect(find.textContaining('both receptacles'), findsOneWidget);
    },
  );
}
