import 'package:monitor_core/monitor_core.dart';
import 'package:test/test.dart';

Map<String, Object?> mm(num v) => {'value': v, 'unit': 'mm', 'src': 'declared'};
Map<String, Object?> w(num v, [String src = 'declared']) => {'value': v, 'unit': 'W', 'src': src};

void main() {
  test('Dorm speaker: the amplifier fits the cabinet, but its cooling does not', () {
    final a = Assembly.fromJson({
      'id': 'dorm', 'name': 'TV cabinet', 'type': 'cabinet',
      'width': mm(450), 'heightMm': mm(200), 'depth': mm(400),
      'ventilation': 'closed', 'rearOpen': false,
      'placements': [
        {'id': 'amp', 'name': 'Amplifier', 'width': mm(430), 'heightMm': mm(90), 'depth': mm(350), 'power': w(300), 'efficiency': 0.7,
         'airflow': 'front-to-back', 'clearance': {'rear': mm(100), 'top': mm(50)}},
      ],
    })!;
    final f = checkPhysical(a);
    final fit = f.where((x) => x.topic == 'fit').single;
    expect(fit.level, 'fail');
    expect(fit.text, startsWith('Amplifier fits, but not with the room its cooling needs'));
    final heat = f.where((x) => x.topic == 'heat').toList();
    expect(heat.firstWhere((x) => x.text.contains('W of heat')).level, 'fail');
    expect(heat.firstWhere((x) => x.text.contains('W of heat')).evidence, 'estimated'); // rule of thumb, not a measurement
    expect(heat.any((x) => x.text.contains('exhaust at the back faces a closed panel')), isTrue);

    // Open the back and give it cooling figures: the airflow finding goes.
    a.extra['rearOpen'] = true;
    expect(checkPhysical(a).any((x) => x.text.contains('closed panel')), isFalse);
  });

  test('too big is a plain fail; a missing dimension is unknown, not ok', () {
    final a = Assembly.fromJson({'id': 'c', 'name': 'shelf', 'type': 'shelf', 'width': mm(300), 'heightMm': mm(100), 'depth': mm(300), 'placements': [
      {'id': 'x', 'name': 'Big box', 'width': mm(400), 'heightMm': mm(90), 'depth': mm(200)},
      {'id': 'y', 'name': 'Mystery', 'width': mm(100)},
    ]})!;
    final f = checkPhysical(a);
    expect(f.firstWhere((x) => x.text.startsWith('Big box')).text, 'Big box does not fit: width 400 mm > 300 mm');
    expect(f.firstWhere((x) => x.text.startsWith('Mystery')).level, 'unknown');
  });

  test('rack: mounting standard, cable length and bend room', () {
    final a = Assembly.fromJson({
      'id': 'r', 'name': 'Rack', 'type': 'rack', 'units': 12, 'depth': mm(450), 'mount': '19in',
      'placements': [
        {'id': 'sw', 'name': 'Switch', 'u': [1, 1], 'depth': mm(250), 'mount': '10in'},
        {'id': 'srv', 'name': 'Server', 'u': [8, 9], 'depth': mm(400), 'mount': '19in'},
      ],
      'cables': [{'name': 'patch', 'from': 'sw', 'to': 'srv', 'length': mm(300), 'minBend': mm(80)}],
    })!;
    final f = checkPhysical(a);
    expect(f.any((x) => x.topic == 'mount' && x.text == 'Switch mounts on 10in; the rack is 19in'), isTrue);
    expect(f.firstWhere((x) => x.topic == 'cable' && x.text.contains('route')).evidence, 'estimated');
    expect(f.any((x) => x.text == 'patch needs 80 mm to bend; there is 50 mm behind the deepest part'), isTrue);
  });

  test('pedalboard: overlap, overhang, wrong voltage and an overloaded outlet', () {
    final a = Assembly.fromJson({
      'id': 'pb', 'name': 'Board', 'type': 'pedalboard', 'width': mm(600), 'depth': mm(300),
      'outlets': [{'id': '1', 'v': 9, 'polarity': 'center-negative', 'ma': 100}, {'id': '2', 'v': 9, 'polarity': 'center-negative', 'ma': 500}],
      'placements': [
        {'id': 'od', 'name': 'Overdrive', 'x': mm(0), 'y': mm(0), 'width': mm(70), 'depth': mm(120), 'outlet': '1', 'v': 9, 'polarity': 'center-negative', 'ma': 20},
        {'id': 'dl', 'name': 'Delay', 'x': mm(50), 'y': mm(0), 'width': mm(100), 'depth': mm(120), 'outlet': '1', 'v': 9, 'polarity': 'center-negative', 'ma': 250},
        {'id': 'fz', 'name': 'Fuzz', 'x': mm(560), 'y': mm(0), 'width': mm(70), 'depth': mm(120), 'outlet': '2', 'v': 18, 'polarity': 'center-negative', 'ma': 10},
      ],
    })!;
    final t = checkPhysical(a).map((x) => x.text).toList();
    expect(t, containsAll(['Overdrive and Delay overlap', 'Fuzz hangs over the edge of the pedalboard', 'Fuzz needs 18 V; outlet 2 gives 9 V', 'outlet 1: 270 mA drawn, it gives 100 mA']));
  });

  test('eurorack: HP per row and rail budget', () {
    final a = Assembly.fromJson({
      'id': 'e', 'name': 'Case', 'type': 'eurorack', 'hp': 84, 'psu': {'+12': 1000, '-12': 500},
      'placements': [
        {'id': 'v', 'name': 'VCO', 'hp': 40, 'ma': {'+12': 300, '-12': 250}},
        {'id': 'f', 'name': 'Filter', 'hp': 50, 'ma': {'+12': 100, '-12': 200}},
      ],
    })!;
    final t = checkPhysical(a).map((x) => x.text).toList();
    expect(t, contains('row 1: modules take 90 HP of 84'));
    expect(t, contains('-12 V rail at 90 % (keep about 20 % spare)'));
  });

  test('declared heat and cooling are verified', () {
    final a = Assembly.fromJson({'id': 'r', 'name': 'Rack', 'type': 'rack', 'cooling': w(200), 'placements': [
      {'id': 'a', 'name': 'NAS', 'heat': w(60)}, {'id': 'b', 'name': 'UPS', 'heat': w(30)},
    ]})!;
    final h = checkPhysical(a).singleWhere((x) => x.topic == 'heat');
    expect((h.level, h.evidence), ('ok', 'verified'));
  });
}
