import 'dart:io';

import 'package:monitor_core/monitor_core.dart';
import 'package:test/test.dart';

void main() {
  late Directory dir;
  late String root;
  setUp(() {
    dir = Directory.systemTemp.createTempSync('notify');
    root = '${dir.path}/Monitor';
    Directory('$root/devices').createSync(recursive: true);
  });
  tearDown(() => dir.deleteSync(recursive: true));

  test('each firing with notify shows once on the devices it is for', () {
    final now = DateTime.now().toUtc();
    final phone = NotifyInbox('${dir.path}/p.json'), laptop = NotifyInbox('${dir.path}/l.json');
    expect(phone.take(root, 'phone', now), isEmpty); // first call only marks the start
    expect(laptop.take(root, 'laptop', now), isEmpty);

    Rule r(String id, List<Map<String, Object?>> then) => Rule.parse({'id': id, 'name': 'Laptop offline', 'when': {'type': 'offline'}, 'then': then}).$1!;
    final reports = {
      'laptop': Report.tryParse({'id': 'laptop', 'name': 'Laptop', 'kind': 'windows', 't': now.subtract(const Duration(hours: 2)).millisecondsSinceEpoch ~/ 1000, 'app': '2.6.9'})!,
      'pc': Report.tryParse({'id': 'pc', 'name': 'pc', 'kind': 'linux', 't': now.millisecondsSinceEpoch ~/ 1000, 'app': '2.6.9'})!,
    };
    final rules = RuleSet([
      r('all', [{'type': 'notify'}]),
      r('phone-only', [{'type': 'notify', 'text': 'Laptop is off', 'on': ['phone']}]),
    ], {});
    final fired = RuleEngine('${dir.path}/e.json').evaluate(root: root, me: 'pc', rules: rules, reports: reports, now: now.add(const Duration(seconds: 1)));
    expect(fired, hasLength(2));

    final later = now.add(const Duration(minutes: 1));
    final p = phone.take(root, 'phone', later);
    expect(p.map((n) => n.text), unorderedEquals([startsWith('Laptop: no report for 2 h'), 'Laptop is off']));
    expect(p.first.title, 'Laptop offline');
    expect(laptop.take(root, 'laptop', later).single.key, startsWith('all|laptop|'));
    expect(phone.take(root, 'phone', later), isEmpty);
    // Hours late (the phone was off): not shown.
    final off = NotifyInbox('${dir.path}/x.json')..take(root, 'x', now.subtract(const Duration(hours: 3)));
    expect(off.take(root, 'x', later.add(const Duration(hours: 2))), isEmpty);
  });
}
