import 'dart:io';

import 'package:flutter_test/flutter_test.dart';
import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';
import 'package:monitor_lite/src/host.dart';
import 'package:monitor_lite/src/setup_page.dart';
import 'package:monitor_lite/src/state.dart';
import 'package:monitor_lite/src/theme.dart';

class _Host extends Fake implements LocalHost {
  _Host(this.folder);
  @override
  String folder;
  @override
  String get id => 'local';
  @override
  String get name => 'Local computer';
  @override
  String get kind => 'windows';
  @override
  String get version => '2.6.6';
  @override
  bool get isAndroid => false;
  @override
  Future<Map<String, Object?>> androidPermissions() async => {};
  @override
  void log(String line) {}
}

void main() {
  testWidgets(
    'fleet requests and acknowledgements stay separate from installed versions',
    (tester) async {
      final root = Directory.systemTemp.createTempSync('monitor-fleet-');
      addTearDown(() => root.deleteSync(recursive: true));
      final state = AppState(_Host(root.path))..latestUpdateVersion = '2.6.6';
      addTearDown(state.dispose);
      Report device(
        String id,
        String name,
        String kind,
        String version, {
        bool stale = false,
      }) {
        return Report(
          id: id,
          name: name,
          kind: kind,
          app: version,
          time: DateTime.now().subtract(Duration(hours: stale ? 1 : 0)),
          controls: kind == 'linux'
              ? []
              : [
                  Control(
                    key: 'update',
                    label: 'Update',
                    options: ['update'],
                    value: version,
                  ),
                ],
        );
      }

      void reports(String phoneVersion) {
        state.reports = {
          'phone': device('phone', 'Phone', 'android', phoneVersion),
          'laptop': device('laptop', 'Laptop', 'windows', '2.6.0'),
          'old': device('old', 'Old computer', 'windows', '2.6.6', stale: true),
          'server': device('server', 'Server', 'linux', '2.6.6'),
        };
        state.assessment = assess(state.reports, myId: state.host.id);
        state.setShowMap(false);
      }

      await tester.binding.setSurfaceSize(const Size(390, 840));
      addTearDown(() => tester.binding.setSurfaceSize(null));
      reports('2.4.0');
      await tester.pumpWidget(
        MaterialApp(theme: buildTheme(), home: SetupPage(state)),
      );
      await tester.pumpAndSettle();
      expect(
        find.text('1 of 3 recently reporting devices on 2.6.6 or newer'),
        findsOneWidget,
      );
      expect(find.text('Last reported version 2.6.6'), findsOneWidget);
      await tester.tap(find.text('Request updates for 2 devices'));
      await tester.pumpAndSettle();
      expect(
        find.textContaining('On Android, open the update notification'),
        findsOneWidget,
      );
      expect(state.folder.myRequests('local'), isEmpty);
      await tester.tap(find.text('Send requests'));
      await tester.pumpAndSettle();
      final requests = state.folder.myRequests('local');
      expect(
        requests.map((request) => request.target),
        unorderedEquals(['laptop', 'phone']),
      );
      expect(find.text('Waiting for device'), findsNWidgets(2));

      final phone = requests.firstWhere((request) => request.target == 'phone');
      state.folder.writeResult(
        phone.host,
        ControlResult(
          id: phone.id,
          ok: true,
          message: 'Update notification posted',
          time: DateTime.now(),
        ),
      );
      state.setShowMap(false);
      await tester.pumpAndSettle();
      expect(find.text('Check the install prompt'), findsOneWidget);
      expect(find.text('Version confirmed'), findsOneWidget);

      reports('2.6.6');
      await tester.pumpAndSettle();
      expect(find.text('Check the install prompt'), findsNothing);
      expect(find.text('Version confirmed'), findsNWidgets(2));
    },
  );
}
