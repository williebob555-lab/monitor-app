import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';
import 'package:monitor_lite/src/network_map_panel.dart';
import 'package:monitor_lite/src/theme.dart';

void main() {
  testWidgets(
    'recorded map uses only earlier readings and has its own device details',
    (tester) async {
      final root = Directory.systemTemp.createTempSync('monitor-map-widget-');
      addTearDown(() => root.deleteSync(recursive: true));
      final folder = SyncFolder(root.path);
      var now = DateTime(2026, 11, 26, 23, 30);
      final before = now.subtract(const Duration(minutes: 62));
      final after = now.subtract(const Duration(minutes: 59));
      final history = HistoryWriter(
        folder.deviceDir('alpha'),
        Directory('${root.path}/spool').path,
      );
      history.add({
        't': before.millisecondsSinceEpoch ~/ 1000,
        'cpu': 23,
      }, before);
      history.add({
        't': after.millisecondsSinceEpoch ~/ 1000,
        'cpu': 88,
      }, after);
      history.flush(now);

      final live = Report(
        id: 'alpha',
        name: 'Alpha',
        kind: 'linux',
        time: now,
        cpuPct: 99,
        tailscaleIp: '100.64.0.1',
      );
      final ups = Report(
        id: 'ups-1',
        name: 'Rack UPS',
        kind: 'thing',
        time: now,
        connected: true,
        node: NodeDecl(id: 'ups-1', type: 'ups', parent: 'alpha'),
      );
      final opened = <String>[];
      Widget app() => MaterialApp(
        theme: buildTheme(),
        home: Scaffold(
          body: MapPanel(
            folder: folder,
            devices: assess({'alpha': live}, now: now).devices,
            things: [thingStatus(Thing(ups, 'alpha'), now: now)],
            myId: 'alpha',
            now: () => now,
            openLive: opened.add,
          ),
        ),
      );

      await tester.binding.setSurfaceSize(const Size(390, 780));
      addTearDown(() => tester.binding.setSurfaceSize(null));
      await tester.pumpWidget(app());
      expect(find.text('Rack UPS'), findsOneWidget);

      tester.widget<Slider>(find.byType(Slider)).onChanged!(23 * 60);
      await tester.pump();
      expect(find.text('Rack UPS'), findsNothing);
      expect(find.text('Syncthing'), findsNothing);
      expect(find.text('Recorded reachability'), findsOneWidget);
      final recorded = tester
          .widget<Text>(find.textContaining('Recorded ·'))
          .data;
      now = now.add(const Duration(minutes: 3));
      await tester.pumpWidget(app());
      expect(
        tester.widget<Text>(find.textContaining('Recorded ·')).data,
        recorded,
      );

      await tester.tap(find.text('Alpha'));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 300));
      expect(opened, isEmpty);
      expect(find.text('CPU 23%'), findsOneWidget);
      expect(find.text('CPU 88%'), findsNothing);
      expect(find.text('CPU 99%'), findsNothing);

      await tester.tap(find.text('Close'));
      await tester.pump();
      await tester.pump(const Duration(milliseconds: 300));
      await tester.tap(find.text('Go live'));
      await tester.pump();
      await tester.tap(find.text('Alpha'));
      expect(opened, ['alpha']);
    },
  );
}
