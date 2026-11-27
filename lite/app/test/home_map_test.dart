import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';
import 'package:monitor_lite/src/home.dart';
import 'package:monitor_lite/src/host.dart';
import 'package:monitor_lite/src/state.dart';
import 'package:monitor_lite/src/theme.dart';

class _FixtureHost extends Fake implements LocalHost {
  _FixtureHost(this.folder);

  @override
  String folder;

  @override
  String get id => 'alpha';
}

void main() {
  testWidgets('Home renders the network banner and opens its live map', (
    tester,
  ) async {
    final root = Directory.systemTemp.createTempSync('monitor-home-widget-');
    addTearDown(() => root.deleteSync(recursive: true));
    await tester.binding.setSurfaceSize(const Size(390, 780));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    final state = AppState(_FixtureHost(root.path));
    addTearDown(state.dispose);
    final now = DateTime.now();
    final local = Report(
      id: 'alpha',
      name: 'Alpha',
      kind: 'windows',
      time: now,
    );
    state.reports = {'alpha': local};
    state.assessment = assess(state.reports, now: now, myId: 'alpha');

    await tester.pumpWidget(
      MaterialApp(theme: buildTheme(), home: HomePage(state)),
    );
    expect(tester.takeException(), isNull);
    expect(find.text(state.assessment.network.title), findsOneWidget);

    await tester.tap(find.byTooltip('Show as a map'));
    await tester.pump();
    expect(tester.takeException(), isNull);
    expect(find.byType(Slider), findsOneWidget);
    expect(find.text('Alpha'), findsOneWidget);
  });
}
