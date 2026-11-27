import 'package:flutter_test/flutter_test.dart';
import 'package:material_ui/material_ui.dart';
import 'package:monitor_lite/src/planning_draft.dart';
import 'package:monitor_lite/src/planning_page.dart';
import 'package:monitor_lite/src/theme.dart';

void main() {
  Finder field(String name) => find.byKey(ValueKey(name));

  Future<void> openWorkspace(
    WidgetTester tester,
    List<ProjectDraft> drafts,
  ) async {
    await tester.binding.setSurfaceSize(const Size(390, 780));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    await tester.pumpWidget(
      MaterialApp(theme: buildTheme(), home: PlanningPage(drafts)),
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
    await openWorkspace(tester, drafts);
    expect(find.textContaining('clear when the app closes'), findsOneWidget);
    await tap(tester, 'New project');
    await tester.enterText(field('Project name'), 'Studio rack');
    await tester.enterText(
      field('Goal'),
      'Move the recording gear off the desk',
    );
    await tester.enterText(field('Rack height (U)'), '4');
    await tap(tester, 'Create project');

    expect(find.text('Move the recording gear off the desk'), findsOneWidget);
    expect(find.textContaining('0 of 4U occupied'), findsOneWidget);
    expect(find.text('Session draft'), findsOneWidget);
    await tap(tester, 'Tasks');
    await tap(tester, 'New task');
    await tester.enterText(field('Task'), 'Measure rear clearance');
    await tap(tester, 'Add task');
    await tap(tester, 'Measure rear clearance');

    await tester.pageBack();
    await tester.pumpAndSettle();
    await tap(tester, 'Studio rack');
    await tap(tester, 'Tasks');
    expect(
      tester.widget<CheckboxListTile>(find.byType(CheckboxListTile)).value,
      true,
    );
  });

  testWidgets('place and move equipment while rejecting an overlap', (
    tester,
  ) async {
    final draft = ProjectDraft('Studio rack', 4, rackDepthMm: 400);
    await openWorkspace(tester, [draft]);
    await tap(tester, draft.name);
    await tap(tester, 'Add at U1');
    await tester.enterText(field('Equipment name'), 'Power amp');
    await tester.enterText(field('Height (U)'), '2');
    await tester.enterText(field('Depth (mm), optional'), '430');
    await tester.enterText(field('Weight (kg), optional'), '9.5');
    await tap(tester, 'Add to project');
    expect(find.textContaining('2 of 4U occupied'), findsOneWidget);
    expect(find.text('Add at U2'), findsNothing);

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
}
