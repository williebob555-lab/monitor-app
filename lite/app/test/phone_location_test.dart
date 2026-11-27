import 'package:flutter_test/flutter_test.dart';
import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';
import 'package:monitor_lite/src/host.dart';
import 'package:monitor_lite/src/phone_location.dart';
import 'package:monitor_lite/src/theme.dart';

class _Host extends Fake implements LocalHost {
  final requested = <bool>[];
  bool on = false;
  @override
  Future<({bool on, bool granted})> locationSharing() async =>
      (on: on, granted: false);
  @override
  Future<void> setLocationSharing(bool enabled) async {
    requested.add(enabled);
    on = enabled;
  }

  @override
  Future<void> nudge({bool live = false}) async {}
}

void main() {
  testWidgets('location starts off and turning it on needs explicit consent', (
    tester,
  ) async {
    final host = _Host();
    await tester.pumpWidget(
      MaterialApp(
        theme: buildTheme(),
        home: Scaffold(body: PhoneLocation(host)),
      ),
    );
    await tester.pumpAndSettle();
    expect(
      tester.widget<SwitchListTile>(find.byType(SwitchListTile)).value,
      false,
    );
    expect(host.requested, isEmpty);
    await tester.tap(find.byType(SwitchListTile));
    await tester.pumpAndSettle();
    expect(
      find.textContaining('Every device with your Monitor folder'),
      findsOneWidget,
    );
    expect(host.requested, isEmpty);
    await tester.tap(find.text('Share location'));
    await tester.pumpAndSettle();
    expect(host.requested, [true]);
    expect(
      find.textContaining('Android location permission is required'),
      findsOneWidget,
    );
    await tester.tap(find.byType(SwitchListTile));
    await tester.pumpAndSettle();
    expect(host.requested, [true, false]);
  });

  test(
    'location shows reported precision and age and rejects malformed values',
    () {
      expect(
        reportedLocation(
          Reading(
            key: 'loc',
            value: '12.22,-14.34',
            note: 'about 1 km; 2 min ago',
          ),
        ),
        '12.22° N · 14.34° W · about 1 km; 2 min ago',
      );
      expect(reportedLocation(Reading(key: 'loc', value: '92,0')), isNull);
    },
  );
}
