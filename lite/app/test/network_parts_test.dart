import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';
import 'package:monitor_lite/src/dive.dart';
import 'package:monitor_lite/src/host.dart';
import 'package:monitor_lite/src/parts.dart';
import 'package:monitor_lite/src/state.dart';
import 'package:monitor_lite/src/theme.dart';

class _Host extends Fake implements LocalHost {
  _Host(this.folder);
  @override
  final String folder;
  @override
  String get id => 'phone';
}

void main() {
  test('missing VPN state stays unknown while collected state is shown', () {
    final now = DateTime.now();
    final older = Report(id: 'pc', name: 'Laptop', kind: 'windows', time: now);
    final unknown = partsOf(older, {}).firstWhere((part) => part.id == 'links');
    expect(unknown.level, Level.inactive);
    expect(unknown.facts, contains(('Tailscale', 'Not reported')));

    final phone = Report(
      id: 'phone',
      name: 'Phone',
      kind: 'android',
      time: now,
      readings: [
        Reading(
          key: 'ts.state',
          value: 'VPN up, no Tailscale address',
          group: 'Tailscale',
        ),
        Reading(key: 'st.state', value: 'Running', group: 'Syncthing'),
        Reading(key: 'drive.SD', value: 22, unit: '%', group: 'Drives'),
      ],
    );
    final parts = partsOf(phone, {});
    final links = parts.firstWhere((part) => part.id == 'links');
    expect(links.level, Level.ok);
    expect(
      links.facts,
      contains(('Tailscale', 'VPN up, no Tailscale address')),
    );
    expect(links.facts, contains(('Syncthing', 'Running')));
    expect(parts.firstWhere((part) => part.id == 'g:Drives').value, '22 %');
  });

  testWidgets('the Network page shows reported checks and timing', (
    tester,
  ) async {
    final folder = Directory.systemTemp.createTempSync('monitor-network');
    addTearDown(() => folder.deleteSync(recursive: true));
    final state = AppState(_Host(folder.path));
    addTearDown(state.dispose);
    state.reports = {
      'phone': Report(
        id: 'phone',
        name: 'Phone',
        kind: 'android',
        time: DateTime.now(),
        readings: [
          Reading(
            key: 'net.dns',
            label: 'Network DNS',
            value: 'no answer',
            group: 'Network',
            level: 'warn',
          ),
          Reading(
            key: 'net.dnsDirect',
            label: 'Public DNS',
            value: 'answers',
            group: 'Network',
            note: '12 ms',
          ),
          Reading(
            key: 'net.ifaces',
            label: 'Interfaces',
            value: 'wlan0',
            group: 'Network',
          ),
        ],
      ),
    };
    state.assessment = assess(state.reports, myId: state.host.id);
    await tester.binding.setSurfaceSize(const Size(390, 800));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    await tester.pumpWidget(
      MaterialApp(
        theme: buildTheme(),
        home: PartPage(state, 'phone', 'network'),
      ),
    );
    expect(find.text('CONNECTION CHECKS'), findsOneWidget);
    expect(find.text('Network DNS · no answer'), findsOneWidget);
    expect(find.text('Public DNS · answers'), findsOneWidget);
    expect(find.text('12 ms'), findsOneWidget);
    expect(find.textContaining('Router ·'), findsNothing);
    expect(
      partsOf(
        state.reports['phone']!,
        {},
      ).firstWhere((part) => part.id == 'network').level,
      Level.warn,
    );
  });
}
