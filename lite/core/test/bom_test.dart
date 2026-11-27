import 'package:monitor_core/monitor_core.dart';
import 'package:test/test.dart';

Map<String, Object?> q(num v, String u) => {'value': v, 'unit': u, 'src': 'declared'};

void main() {
  // Scenario 2: a 12U rack for the server, switch and audio interface.
  Item it(String id, String name, String state, List<Map<String, Object?>> ports) => Item(id: id, type: 'device', name: name, state: state, ports: ports);
  final items = {
    for (final i in [
      it('srv', 'wc-desktop', 'owned', [
        {'id': 'eth0', 'kind': 'rj45', 'dir': 'both', 'signals': ['eth-1g'], 'required': true},
        {'id': 'psu', 'kind': 'iec-c14', 'dir': 'in', 'signals': ['ac-mains-10a'], 'required': true},
        {'id': 'usb1', 'kind': 'usb-a', 'dir': 'out', 'signals': ['usb-2', 'usb-3-5g']},
      ]),
      it('sw', '8-port switch', 'owned', [
        {'id': 'p1', 'kind': 'rj45', 'dir': 'both', 'signals': ['eth-1g']},
      ]),
      it('motu', 'MOTU interface', 'owned', [
        {'id': 'usb', 'kind': 'usb-c', 'dir': 'in', 'signals': ['usb-2']},
      ]),
      it('ups', 'Rack UPS 1500 VA', 'proposed', [
        {'id': 'out1', 'kind': 'iec-c13', 'gender': 'receptacle', 'dir': 'out', 'signals': ['ac-mains-10a']},
      ]),
      it('c13', 'C13 power cable', 'owned', [{'id': 'a', 'kind': 'iec-c13'}, {'id': 'b', 'kind': 'iec-c13'}]),
    ])
      i.id: i,
  };
  final project = Project.fromJson({
    'id': 'rack', 'name': 'Server rack',
    'tasks': [{'id': 't1', 'text': 'Buy rails', 'done': false}, {'id': 't2', 'text': 'Measure the closet', 'done': true}],
    'assemblies': [
      {'id': 'r', 'name': '12U rack', 'type': 'rack', 'units': 12, 'depth': q(600, 'mm'), 'maxPower': q(300, 'W'),
       'placements': [
         {'id': 'p-ups', 'name': 'UPS', 'item': 'ups', 'u': [1, 2], 'power': q(20, 'W')},
         {'id': 'p-srv', 'name': 'Server', 'item': 'srv', 'u': [3, 6], 'power': q(250, 'W')},
         {'id': 'p-sw', 'name': 'Switch', 'item': 'sw', 'u': [7, 7], 'power': q(10, 'W')},
         {'id': 'p-motu', 'name': 'MOTU', 'item': 'motu', 'u': [8, 8], 'power': q(30, 'W')},
         {'id': 'p-shelf', 'name': '1U shelf', 'u': [9, 9]},
       ]},
    ],
    'connections': [
      {'id': 'net', 'from': {'placement': 'p-sw', 'port': 'p1'}, 'to': {'placement': 'p-srv', 'port': 'eth0'}, 'required': true},
      {'id': 'audio', 'from': {'placement': 'p-srv', 'port': 'usb1'}, 'to': {'placement': 'p-motu', 'port': 'usb'}, 'required': true,
       'via': [{'name': 'USB-A cable', 'a': 'usb-a', 'b': 'usb-a', 'passes': ['usb-2']}]},
    ],
  })!;

  test('the rack scenario lists exactly what is missing', () {
    final n = projectNeeds(project, items);
    expect(n.toBuy.map((l) => '${l.qty} x ${l.name}: ${l.status}').toList(), [
      '1 x Rack UPS 1500 VA: to-buy',
      '1 x 1U shelf: to-buy',
      '1 x 8P8C (RJ45) cable: missing',
      '1 x USB-A cable: to-buy',
      '1 x adapter: missing',
    ]);
    expect(n.lines.where((l) => l.status == 'owned').map((l) => l.name), ['wc-desktop', '8-port switch', 'MOTU interface']);
    expect(n.connectionProblems['audio']!.single, 'a USB Type-A plug does not go into a USB Type-C');
    expect(n.unconnected, ['Server: psu is required and not connected']);
    expect(n.physical.single.text, contains('310 W is over the 300 W limit'));
    expect(n.openTasks, ['Buy rails']);
  });

  test('the right cable clears the connection; an owned cable is not to buy', () {
    final p = Project.fromJson({
      ...project.toJson(),
      'connections': [
        {'id': 'power', 'from': {'placement': 'p-ups', 'port': 'out1'}, 'to': {'placement': 'p-srv', 'port': 'psu'}, 'via': [{'item': 'c13', 'a': 'iec-c14', 'b': 'iec-c13'}]},
      ],
    })!;
    final n = projectNeeds(p, items);
    expect(n.connectionProblems, isEmpty);
    expect(n.lines.firstWhere((l) => l.name == 'C13 power cable').status, 'owned');
    expect(n.unconnected, ['Server: eth0 is required and not connected']);
  });
}
