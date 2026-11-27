import 'package:flutter_test/flutter_test.dart';
import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';
import 'package:monitor_lite/src/map_view.dart';

void main() {
  final now = DateTime.utc(2026, 11, 27);

  List<DeviceStatus> twins(DateTime linuxTime, DateTime windowsTime) {
    final reports = {
      'alpha': Report(
        id: 'alpha',
        name: 'Alpha',
        kind: 'linux',
        os: 'Fedora',
        machine: 'machine-1',
        time: linuxTime,
        tailscaleIp: '100.64.0.1',
      ),
      'desktop': Report(
        id: 'desktop',
        name: 'Desktop',
        kind: 'windows',
        os: 'Windows',
        machine: 'machine-1',
        time: windowsTime,
        tailscaleIp: '100.64.0.2',
      ),
    };
    return assess(reports, myId: 'alpha', now: now).devices;
  }

  Future<void> showMap(
    WidgetTester tester,
    List<DeviceStatus> devices,
    ValueChanged<String> onOpen, {
    List<DeviceStatus> things = const [],
  }) async {
    await tester.binding.setSurfaceSize(const Size(390, 780));
    addTearDown(() => tester.binding.setSurfaceSize(null));
    await tester.pumpWidget(MaterialApp(
      home: Scaffold(
        body: NetworkMap(devices, myId: 'alpha', things: things, onOpen: onOpen),
      ),
    ));
    await tester.pump();
  }

  testWidgets('fresh twin reports share a host and open their own pages', (tester) async {
    final opened = <String>[];
    await showMap(tester, twins(now, now.subtract(const Duration(seconds: 30))), opened.add);

    expect(find.text('1 device'), findsOneWidget);
    expect(find.text('Running · Online'), findsOneWidget);
    expect(find.text('Windows · Not booted'), findsOneWidget);
    await tester.tap(find.byKey(const ValueKey('os:desktop')));
    await tester.tap(find.text('Alpha'));
    expect(opened, ['desktop', 'alpha']);
  });

  testWidgets('stale twin reports do not claim a running OS', (tester) async {
    final opened = <String>[];
    await showMap(
      tester,
      twins(now.subtract(const Duration(hours: 2)), now.subtract(const Duration(hours: 1))),
      opened.add,
    );

    expect(find.text('Fedora · Other install'), findsOneWidget);
    expect(find.textContaining('Last reported'), findsOneWidget);
    expect(find.textContaining('Running ·'), findsNothing);
    expect(find.text('Attention needed · 1'), findsOneWidget);
  });

  testWidgets('hardware reported by the other install remains on the map', (tester) async {
    final ups = Report(
      id: 'ups-1',
      name: 'Rack UPS',
      kind: 'thing',
      time: now,
      connected: true,
      node: NodeDecl(
        id: 'ups-1',
        type: 'ups',
        parent: 'desktop',
        links: [NodeLink(to: 'desktop', via: 'power', dir: 'out')],
      ),
      readings: [Reading(key: 'ups.status', value: 'battery, low', level: 'warn')],
    );
    await showMap(
      tester,
      twins(now, now.subtract(const Duration(seconds: 30))),
      (_) {},
      things: [thingStatus(Thing(ups, 'desktop'), now: now)],
    );

    expect(find.text('Rack UPS'), findsOneWidget);
    expect(find.text('Attention needed · 1'), findsOneWidget);
  });
}
