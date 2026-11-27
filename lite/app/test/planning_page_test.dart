import 'dart:io';

import 'package:flutter/services.dart';
import 'package:flutter_test/flutter_test.dart';
import 'package:material_ui/material_ui.dart';
import 'package:monitor_lite/src/planning_draft.dart';
import 'package:monitor_lite/src/planning_page.dart';
import 'package:monitor_lite/src/project_needs_page.dart';
import 'package:monitor_lite/src/theme.dart';
import 'package:monitor_core/monitor_core.dart';

void main() {
  testWidgets('project needs shows owned items and what is still to buy', (
    tester,
  ) async {
    final folder = Directory.systemTemp.createTempSync('monitor-project-needs');
    addTearDown(() => folder.deleteSync(recursive: true));
    final store = ProjectStore(folder.path, 'laptop');
    ModelStore(
      folder.path,
      'laptop',
    ).put(Item(id: 'ups', type: 'ups', name: 'Rack UPS', state: 'owned'));
    final project = Project(
      id: 'studio',
      name: 'Studio rack',
      assemblies: [
        Assembly(
          id: 'rack',
          name: 'Rack',
          units: 4,
          placements: [
            Placement(id: 'planned', name: 'Spare amplifier'),
            Placement(id: 'owned', name: 'UPS', item: 'ups'),
          ],
        ),
      ],
    );
    await tester.binding.setSurfaceSize(const Size(390, 780));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    await tester.runAsync(() async {
      await tester.pumpWidget(
        MaterialApp(
          theme: buildTheme(),
          home: ProjectNeedsPage(project, store),
        ),
      );
      for (var i = 0; i < 100 && find.text('TO BUY').evaluate().isEmpty; i++) {
        await Future<void>.delayed(const Duration(milliseconds: 20));
        await tester.pump();
      }
    });
    expect(find.text('TO BUY'), findsOneWidget);
    expect(find.text('1 × Spare amplifier'), findsOneWidget);
    expect(find.text('ON HAND'), findsOneWidget);
    expect(find.text('1 × Rack UPS'), findsOneWidget);
  });

  Finder field(String name) => find.byKey(ValueKey(name));

  Future<void> openWorkspace(
    WidgetTester tester,
    List<ProjectDraft> drafts, {
    Size size = const Size(390, 780),
    ProjectStore? store,
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
          store: store,
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

  testWidgets('saved racks and tasks reopen for another device', (
    tester,
  ) async {
    final folder = Directory.systemTemp.createTempSync('monitor-projects');
    addTearDown(() => folder.deleteSync(recursive: true));
    final first = ProjectStore(folder.path, 'laptop');
    final second = ProjectStore(folder.path, 'tablet');
    await openWorkspace(tester, [], store: first);
    await tap(tester, 'New project');
    await tester.enterText(field('Project name'), 'Studio rack');
    await tester.enterText(field('Rack height (U), optional'), '4');
    await tap(tester, 'Create project');
    await tester.tap(find.byKey(const ValueKey('rack-empty:1')));
    await tester.pumpAndSettle();
    await tester.enterText(field('Equipment name'), 'Interface');
    await tap(tester, 'Add to project');
    await tap(tester, 'Tasks');
    await tap(tester, 'New task');
    await tester.enterText(field('Task'), 'Connect interface');
    await tap(tester, 'Add task');
    await tap(tester, 'Connect interface');
    await tester.tap(find.byTooltip('Save project'));
    await tester.pumpAndSettle();
    expect(find.text('Saved to Monitor folder'), findsOneWidget);
    await tap(tester, 'Equipment');
    await tap(tester, 'Add equipment');
    await tester.enterText(field('Equipment name'), 'Spare amplifier');
    await tester.enterText(field('Height (U), optional'), '2');
    await tap(tester, 'Add to project');
    await tester.tap(find.byTooltip('Save project'));
    await tester.pumpAndSettle();
    final saved = second.list().single;
    expect(saved.tasks.single.done, true);
    final placements = saved.assemblies.single.placements;
    expect(placements.first.u, (1, 1));
    expect(placements.last.u, isNull);
    expect(placements.last.height, 2);

    await tester.pumpWidget(const SizedBox.shrink());
    await openWorkspace(tester, [], store: second);
    await tap(tester, 'Studio rack');
    expect(find.text('Interface'), findsOneWidget);
    await tap(tester, 'Equipment');
    expect(find.text('Spare amplifier'), findsOneWidget);
    expect(find.textContaining('2U · Unplaced'), findsOneWidget);
    await tap(tester, 'Tasks');
    expect(
      tester.widget<CheckboxListTile>(find.byType(CheckboxListTile)).value,
      true,
    );
  });

  test(
    'saving preserves the original quantities and rejects a newer revision',
    () {
      final folder = Directory.systemTemp.createTempSync(
        'monitor-project-revisions',
      );
      addTearDown(() => folder.deleteSync(recursive: true));
      final first = ProjectStore(folder.path, 'laptop');
      final second = ProjectStore(folder.path, 'tablet');
      final saved = first.put(
        Project(
          id: 'studio',
          name: 'Studio',
          assemblies: [
            Assembly(
              id: 'back',
              name: 'Rear rack',
              units: 4,
              placements: [
                Placement(
                  id: 'ups',
                  name: 'UPS',
                  face: 'rear',
                  thing: 'power',
                  u: (1, 2),
                  depth: Qty(14, 'in', src: 'measured'),
                ),
              ],
            ),
            Assembly(id: 'pedals', name: 'Pedalboard', type: 'board'),
          ],
        ),
      );
      final draft = ProjectDraft.fromProject(saved)..name = 'Renamed';
      draft.save(first);
      draft.rackUnits = 6;
      draft.save(first);
      final copy = second.get('studio')!;
      expect(copy.assemblies.first.units, 6);
      expect(copy.assemblies.map((a) => a.id), ['back', 'pedals']);
      final placement = copy.assemblies.first.placements.single;
      expect(
        (placement.depth?.value, placement.depth?.unit, placement.depth?.src),
        (14, 'in', 'measured'),
      );
      expect((placement.face, placement.thing), ('rear', 'power'));
      second.put(copy..name = 'Changed on tablet');
      expect(() => draft.save(first), throwsStateError);
      expect(first.get('studio')!.name, 'Changed on tablet');
    },
  );

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
