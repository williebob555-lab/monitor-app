import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:material_ui/material_ui.dart';
import 'package:monitor_lite/src/planning_draft.dart';
import 'package:monitor_lite/src/planning_page.dart';
import 'package:monitor_lite/src/theme.dart';

void main() {
  Finder field(String name) => find.byKey(ValueKey(name));

  Future<void> openWorkspace(
    WidgetTester tester,
    List<ProjectDraft> drafts, {
    Size size = const Size(390, 780),
    Map<String, String> Function()? monitoredEquipment,
    void Function(BuildContext, String)? openMonitorReport,
  }) async {
    await tester.binding.setSurfaceSize(size);
    addTearDown(() => tester.binding.setSurfaceSize(null));
    await tester.pumpWidget(
      MaterialApp(
        theme: buildTheme(),
        home: PlanningPage(
          drafts,
          monitoredEquipment: monitoredEquipment,
          openMonitorReport: openMonitorReport,
        ),
      ),
    );
  }

  Future<void> tap(WidgetTester tester, String text) async {
    await tester.tap(find.text(text));
    await tester.pumpAndSettle();
  }

  testWidgets('create a rack project and reopen its task checklist', (
    tester,
  ) async {
    final drafts = <ProjectDraft>[];
    String? copied;
    final messages = tester.binding.defaultBinaryMessenger;
    messages.setMockMethodCallHandler(SystemChannels.platform, (call) async {
      if (call.method == 'Clipboard.setData') {
        copied = (call.arguments as Map)['text'] as String;
      }
      return null;
    });
    addTearDown(
      () => messages.setMockMethodCallHandler(SystemChannels.platform, null),
    );
    await openWorkspace(tester, drafts);
    expect(find.textContaining('clear when the app closes'), findsOneWidget);
    await tap(tester, 'New project');
    await tester.enterText(field('Project name'), 'Studio rack');
    await tester.enterText(
      field('Goal'),
      'Move the recording gear off the desk',
    );
    await tester.enterText(field('Rack height (U), optional'), '4');
    await tap(tester, 'Create project');

    expect(find.text('Move the recording gear off the desk'), findsOneWidget);
    expect(find.textContaining('0 of 4U occupied'), findsOneWidget);
    expect(find.text('Session draft'), findsOneWidget);
    await tap(tester, 'Tasks');
    await tap(tester, 'New task');
    await tester.enterText(field('Task'), 'Measure rear clearance');
    await tap(tester, 'Add task');
    await tap(tester, 'Measure rear clearance');
    await tester.tap(find.byTooltip('Copy plan'));
    await tester.pump();
    expect(copied, contains('Rack · 4U · depth unknown'));
    expect(copied, contains('- [x] Measure rear clearance'));

    await tester.pageBack();
    await tester.pumpAndSettle();
    await tap(tester, 'Studio rack');
    await tap(tester, 'Tasks');
    expect(
      tester.widget<CheckboxListTile>(find.byType(CheckboxListTile)).value,
      true,
    );
  });

  testWidgets('capture equipment before a rack or measurements are known', (
    tester,
  ) async {
    await openWorkspace(tester, []);
    await tap(tester, 'New project');
    await tester.enterText(field('Project name'), 'Portable recording');
    await tap(tester, 'Create project');
    expect(find.text('Add a rack'), findsOneWidget);

    await tap(tester, 'Equipment');
    await tap(tester, 'Add equipment');
    await tester.enterText(field('Equipment name'), 'Field recorder');
    await tap(tester, 'Add to project');
    expect(find.text('Field recorder'), findsOneWidget);
    expect(find.textContaining('Height unknown · Unplaced'), findsOneWidget);

    await tap(tester, 'Rack');
    await tap(tester, 'Add a rack');
    await tester.enterText(field('Rack height (U), optional'), '4');
    await tap(tester, 'Apply changes');
    expect(find.textContaining('0 of 4U occupied'), findsOneWidget);
  });

  testWidgets('place and move equipment while rejecting an overlap', (
    tester,
  ) async {
    final draft = ProjectDraft('Studio rack', rackUnits: 4, rackDepthMm: 400);
    await openWorkspace(tester, [draft]);
    await tap(tester, draft.name);
    await tester.tap(find.byKey(const ValueKey('rack-empty:1')));
    await tester.pumpAndSettle();
    await tester.enterText(field('Equipment name'), 'Power amp');
    await tester.enterText(field('Height (U), optional'), '2');
    await tester.enterText(field('Depth (mm), optional'), '430');
    await tester.enterText(field('Weight (kg), optional'), '9.5');
    await tap(tester, 'Add to project');
    expect(find.textContaining('2 of 4U occupied'), findsOneWidget);
    expect(find.byKey(const ValueKey('rack-empty:2')), findsNothing);
    expect(find.byIcon(Icons.warning_amber_rounded), findsOneWidget);

    await tap(tester, 'Equipment');
    expect(
      find.text('Weight entered for 1 of 1 entries · 9.5 kg'),
      findsOneWidget,
    );
    expect(find.text('Power unknown'), findsOneWidget);
    expect(
      find.textContaining('Depth exceeds entered rack depth'),
      findsOneWidget,
    );
    await tap(tester, 'Add equipment');
    await tester.enterText(field('Equipment name'), 'Patch panel');
    await tester.enterText(field('Height (U), optional'), '1');
    await tester.enterText(field('Bottom unit, optional'), '2');
    await tap(tester, 'Add to project');
    expect(find.text('This position overlaps Power amp.'), findsOneWidget);

    await tester.enterText(field('Bottom unit, optional'), '3');
    await tap(tester, 'Add to project');
    await tap(tester, 'Power amp');
    await tester.enterText(field('Bottom unit, optional'), '');
    await tap(tester, 'Apply changes');
    expect(find.textContaining('2U · Unplaced'), findsOneWidget);

    await tap(tester, 'Rack');
    expect(find.textContaining('1 of 4U occupied'), findsOneWidget);
    expect(find.text('Patch panel'), findsOneWidget);
    expect(find.text('Power amp'), findsNothing);
  });

  testWidgets('renaming a draft item keeps its link to the monitor report', (
    tester,
  ) async {
    final opened = <String>[];
    await openWorkspace(
      tester,
      [ProjectDraft('Power upgrade')],
      size: const Size(1200, 800),
      monitoredEquipment: () => {'ups-a': 'Rack UPS'},
      openMonitorReport: (_, id) => opened.add(id),
    );
    await tap(tester, 'Power upgrade');
    await tap(tester, 'Equipment');
    await tap(tester, 'Add from Monitor');
    await tap(tester, 'Rack UPS');
    await tester.enterText(field('Equipment name'), 'Bottom rack UPS');
    await tap(tester, 'Add to project');
    expect(find.text('Bottom rack UPS'), findsOneWidget);
    await tap(tester, 'Open monitor report');
    expect(opened, ['ups-a']);
  });
}
