import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';
import 'package:monitor_lite/src/rule_form.dart';
import 'package:monitor_lite/src/rules_page.dart';
import 'package:monitor_lite/src/theme.dart';

void main() {
  testWidgets(
    'editing a rule preserves multiple destinations and saved properties',
    (tester) async {
      final root = Directory.systemTemp.createTempSync('monitor-rule-edit');
      addTearDown(() => root.deleteSync(recursive: true));
      final previous = Rule(
        id: 'offline',
        name: 'Offline notice',
        enabled: false,
        when: {'type': 'offline', 'for': 623},
        scope: {
          'devices': ['laptop', 'tablet'],
        },
        then: [
          {
            'type': 'notify',
            'on': ['tablet', 'laptop'],
            'text': 'Please check the device.',
          },
        ],
        cooldown: 308,
        extra: {'origin': 'test'},
      );
      RuleSet.save(root.path, previous);
      await tester.binding.setSurfaceSize(const Size(390, 780));
      addTearDown(() => tester.binding.setSurfaceSize(null));
      await tester.pumpWidget(
        MaterialApp(
          theme: buildTheme(),
          home: Scaffold(
            body: Builder(
              builder: (context) => TextButton(
                onPressed: () => Navigator.of(context).push<bool>(
                  MaterialPageRoute(
                    builder: (_) => RuleForm(
                      root.path,
                      const {},
                      const {},
                      existing: previous,
                    ),
                  ),
                ),
                child: const Text('Open existing'),
              ),
            ),
          ),
        ),
      );
      await tester.tap(find.text('Open existing'));
      await tester.pumpAndSettle();
      await tester.enterText(
        find.byKey(const ValueKey('name')),
        'Renamed notice',
      );
      await tester.scrollUntilVisible(
        find.text('Save rule'),
        120,
        scrollable: find.byType(Scrollable).first,
      );
      await tester.tap(find.text('Save rule'));
      await tester.pumpAndSettle();
      final saved = RuleSet.load(root.path).rules.single;
      expect(saved.name, 'Renamed notice');
      expect(saved.when, previous.when);
      expect(saved.scope, previous.scope);
      expect(saved.then, previous.then);
      expect(saved.cooldown, 308);
      expect(saved.extra, previous.extra);
    },
  );
  testWidgets('new rule writes the displayed condition and starts disabled', (
    tester,
  ) async {
    final root = Directory.systemTemp.createTempSync('monitor-rules');
    addTearDown(() => root.deleteSync(recursive: true));
    await tester.binding.setSurfaceSize(const Size(390, 780));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    await tester.pumpWidget(
      MaterialApp(
        theme: buildTheme(),
        home: Scaffold(
          body: Builder(
            builder: (context) => TextButton(
              onPressed: () => Navigator.of(context).push<bool>(
                MaterialPageRoute(
                  builder: (_) =>
                      RuleForm(root.path, const {'laptop': 'Laptop'}, const {}),
                ),
              ),
              child: const Text('Open form'),
            ),
          ),
        ),
      ),
    );
    await tester.tap(find.text('Open form'));
    await tester.pumpAndSettle();
    await tester.enterText(
      find.byKey(const ValueKey('name')),
      'Offline notice',
    );
    await tester.scrollUntilVisible(
      find.text('Save rule'),
      120,
      scrollable: find.byType(Scrollable).first,
    );
    await tester.tap(find.text('Save rule'));
    await tester.pumpAndSettle();
    final rule = RuleSet.load(root.path).rules.single;
    expect(rule.name, 'Offline notice');
    expect(rule.enabled, false);
    expect(rule.when, {'type': 'offline', 'for': 600});
    expect(rule.then.single['type'], 'notify');
  });

  testWidgets('rule detail shows the evaluator’s recorded reason', (
    tester,
  ) async {
    final root = Directory.systemTemp.createTempSync('monitor-rule-evidence');
    addTearDown(() => root.deleteSync(recursive: true));
    final rule = Rule(
      id: 'battery-low',
      name: 'Battery low',
      when: {'type': 'value', 'path': 'battery.pct', 'op': '<', 'value': 10},
      then: [
        {'type': 'notify'},
      ],
    );
    RuleSet.save(root.path, rule);
    final now = DateTime.now();
    final report = Report(
      id: 'laptop',
      name: 'Laptop',
      kind: 'windows',
      time: now,
      app: '2.6.9',
      battery: Battery(pct: 7),
    );
    RuleEngine('${root.path}/state.json').evaluate(
      root: root.path,
      me: report.id,
      rules: RuleSet.load(root.path),
      reports: {report.id: report},
      now: now,
    );
    await tester.runAsync(() async {
      await tester.pumpWidget(
        MaterialApp(
          theme: buildTheme(),
          home: RulesPage(root.path, const {'laptop': 'Laptop'}, const {}),
        ),
      );
      for (
        var i = 0;
        i < 100 && find.text('Battery low').evaluate().isEmpty;
        i++
      ) {
        await Future<void>.delayed(const Duration(milliseconds: 20));
        await tester.pump();
      }
      await tester.tap(find.text('Battery low'));
      for (
        var i = 0;
        i < 100 &&
            find.textContaining('Evaluated by Laptop').evaluate().isEmpty;
        i++
      ) {
        await Future<void>.delayed(const Duration(milliseconds: 20));
        await tester.pump(const Duration(milliseconds: 20));
      }
    });
    expect(find.textContaining('battery.pct is 7'), findsOneWidget);
    expect(find.textContaining('Evaluated by Laptop'), findsOneWidget);
  });
}
