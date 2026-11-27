import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';
import 'package:monitor_lite/src/dive.dart';
import 'package:monitor_lite/src/history_coverage.dart';
import 'package:monitor_lite/src/host.dart';
import 'package:monitor_lite/src/state.dart';
import 'package:monitor_lite/src/theme.dart';

class _Host extends Fake implements LocalHost {
  _Host(this.folder);
  @override
  String folder;
  @override
  String get id => 'alpha';
}

void main() {
  test('missing slots are retained while totals use all archive samples', () {
    final end = DateTime.utc(2026, 11, 27, 12);
    final start =
        end.subtract(const Duration(days: 1)).millisecondsSinceEpoch ~/ 1000;
    final window = HistoryCoverage(HistoryRange.day, end, [
      {'t': start - 1, 'cpu': 99},
      {'t': start + 10, 'cpu': 1},
      {'t': start + 230, 'cpu': 2},
      {'t': start + 600, 'cpu': 0},
      {'t': start + 601, 'mem': 30},
      {'t': start + 86401, 'cpu': 99},
    ]);
    expect(window.occupied.take(4), [true, false, true, false]);
    expect(window.plot('cpu').take(4), [2, null, 0, null]);
    expect(window.values('cpu'), [1, 2, 0, null]);
  });

  testWidgets('part history displays coverage from the actual archive reader', (
    tester,
  ) async {
    final root = Directory.systemTemp.createTempSync('monitor-coverage-');
    addTearDown(() => root.deleteSync(recursive: true));
    final state = AppState(_Host(root.path));
    addTearDown(state.dispose);
    final now = DateTime.now();
    final local = Report(
      id: 'alpha',
      name: 'Alpha',
      kind: 'linux',
      time: now,
      cpuPct: 55,
    );
    state.reports = {'alpha': local};
    state.assessment = assess(state.reports, myId: state.host.id);
    final writer = HistoryWriter(
      state.folder.deviceDir('alpha'),
      '${root.path}/spool',
    );
    for (final (hoursAgo, sample) in [(6, 22), (1, 35)]) {
      final time = now.subtract(Duration(hours: hoursAgo));
      writer.add({
        't': time.millisecondsSinceEpoch ~/ 1000,
        'cpu': sample,
      }, time);
    }
    writer.flush(now);
    await tester.binding.setSurfaceSize(const Size(390, 800));
    addTearDown(() => tester.binding.setSurfaceSize(null));

    await tester.pumpWidget(
      MaterialApp(theme: buildTheme(), home: PartPage(state, 'alpha', 'cpu')),
    );
    expect(find.text('Archive coverage'), findsOneWidget);
    expect(find.text('2 of 288 slots'), findsOneWidget);
    expect(
      find.text('5-minute slots · dark sections have no archived sample'),
      findsOneWidget,
    );
    await tester.tap(find.text('7 days'));
    await tester.pumpAndSettle();
    expect(
      find.text('hourly slots · dark sections have no archived sample'),
      findsOneWidget,
    );
  });
}
