import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';
import 'package:monitor_lite/src/dive.dart';
import 'package:monitor_lite/src/home.dart';
import 'package:monitor_lite/src/host.dart';
import 'package:monitor_lite/src/map_view.dart';
import 'package:monitor_lite/src/state.dart';
import 'package:monitor_lite/src/theme.dart';

class _Host extends Fake implements LocalHost {
  _Host(this.folder);
  @override
  final String folder;
  @override
  String get id => 'windows';
}

void main() {
  AppState fixture(DateTime now) {
    final root = Directory.systemTemp.createTempSync('monitor-machine');
    addTearDown(() => root.deleteSync(recursive: true));
    final state = AppState(_Host(root.path));
    addTearDown(state.dispose);
    state.reports = {
      'windows': Report(
        id: 'windows',
        name: 'Laptop',
        kind: 'windows',
        os: 'Windows 11',
        machine: 'shared',
        time: now,
        cpuPct: 17,
      ),
      'fedora': Report(
        id: 'fedora',
        name: 'Laptop Fedora',
        kind: 'linux',
        os: 'Fedora',
        machine: 'shared',
        time: now.subtract(const Duration(hours: 3)),
        cpuPct: 52,
      ),
    };
    state.assessment = assess(state.reports, myId: state.host.id, now: now);
    return state;
  }

  testWidgets('Home has one card for the physical laptop', (tester) async {
    final state = fixture(DateTime.now())..showMap = false;
    await tester.binding.setSurfaceSize(const Size(390, 840));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    await tester.pumpWidget(
      MaterialApp(theme: buildTheme(), home: HomePage(state)),
    );
    expect(find.byType(DeviceCard), findsOneWidget);
    expect(find.text('Laptop'), findsOneWidget);
    expect(find.text('Also boots Fedora'), findsOneWidget);
    expect(find.text('Laptop Fedora'), findsNothing);
  });

  testWidgets('Map counts one machine even when both boot systems are silent', (
    tester,
  ) async {
    final state = fixture(DateTime.now().subtract(const Duration(hours: 4)));
    state.assessment = assess(state.reports, myId: state.host.id);
    await tester.binding.setSurfaceSize(const Size(390, 840));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    await tester.pumpWidget(
      MaterialApp(
        theme: buildTheme(),
        home: Scaffold(
          body: NetworkMap(
            state.assessment.devices,
            myId: state.host.id,
            onOpen: (_) {},
          ),
        ),
      ),
    );
    await tester.pump(const Duration(milliseconds: 200));
    expect(find.text('1 device'), findsOneWidget);
    expect(find.text('Laptop Fedora'), findsNothing);
  });

  testWidgets('one history view shows recorded readings from both systems', (
    tester,
  ) async {
    final now = DateTime.now();
    final state = fixture(now);
    for (final (id, sample, hour) in [('windows', 31, 1), ('fedora', 62, 4)]) {
      final at = now.subtract(Duration(hours: hour));
      final writer = HistoryWriter(
        state.dirOf(id),
        '${state.folder.root}/spool-$id',
      );
      writer.add({'t': at.millisecondsSinceEpoch ~/ 1000, 'cpu': sample}, at);
      writer.flush(now);
    }
    await tester.binding.setSurfaceSize(const Size(390, 840));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    await tester.runAsync(() async {
      await tester.pumpWidget(
        MaterialApp(
          theme: buildTheme(),
          home: PartPage(state, 'windows', 'cpu'),
        ),
      );
      for (var i = 0; i < 100 && find.text('Fedora').evaluate().isEmpty; i++) {
        await Future<void>.delayed(const Duration(milliseconds: 20));
        await tester.pump();
      }
    });
    expect(find.text('Windows 11'), findsOneWidget);
    expect(find.text('Fedora'), findsOneWidget);
    final graphs = tester.widgetList<Chart>(find.byType(Chart));
    expect(
      graphs.map((graph) => graph.values.toList()),
      containsAll([
        [31],
        [62],
      ]),
    );
  });
}
