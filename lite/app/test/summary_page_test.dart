import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';
import 'package:monitor_lite/src/host.dart';
import 'package:monitor_lite/src/state.dart';
import 'package:monitor_lite/src/summary_page.dart';
import 'package:monitor_lite/src/theme.dart';

class _Host extends Fake implements LocalHost {
  _Host(this.folder);
  @override
  final String folder;
  @override
  String get id => 'laptop';
}

void main() {
  test('offers a summary only when an active computer reports a model', () {
    final now = DateTime.now();
    Report host(String kind, DateTime time) => Report(
      id: 'device',
      name: 'Device',
      kind: kind,
      time: time,
      readings: [Reading(key: 'ai.models', value: 'test-model:3b')],
    );
    expect(canSummarize([host('windows', now)], now), true);
    expect(canSummarize([host('android', now)], now), false);
    expect(
      canSummarize([host('linux', now.subtract(const Duration(days: 1)))], now),
      false,
    );
  });

  testWidgets(
    'shows a recorded summary with its answering computer and model',
    (tester) async {
      final root = Directory.systemTemp.createTempSync('monitor-local-summary');
      addTearDown(() => root.deleteSync(recursive: true));
      final state = AppState(_Host(root.path));
      addTearDown(state.dispose);
      final now = DateTime.now();
      const model = 'fixture:3b';
      final server = Report(
        id: 'server',
        name: 'Server',
        kind: 'linux',
        time: now,
        readings: [Reading(key: 'ai.models', value: model)],
      );
      state.reports = {server.id: server};
      final id = requestAi(root.path, 'laptop', 'week-summary', now: now);
      state.summaryRequests[root.path] = (id, now);
      await answerAiRequests(
        root.path,
        server.id,
        state.reports,
        [model],
        now,
        http: (method, url, headers, body) async {
          expect(url.host, '127.0.0.1');
          return (200, '{"response":"The server recovered."}');
        },
      );
      await tester.runAsync(() async {
        await tester.pumpWidget(
          MaterialApp(theme: buildTheme(), home: SummaryPage(state)),
        );
        for (
          var i = 0;
          i < 100 && find.text('The server recovered.').evaluate().isEmpty;
          i++
        ) {
          await Future<void>.delayed(const Duration(milliseconds: 20));
          await tester.pump();
        }
      });
      expect(find.text('The server recovered.'), findsOneWidget);
      expect(find.text('From Server'), findsOneWidget);
      expect(find.text(model), findsOneWidget);
    },
  );
}
