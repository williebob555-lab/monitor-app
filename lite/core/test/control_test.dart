import 'dart:io';

import 'package:monitor_core/monitor_core.dart';
import 'package:test/test.dart';

void main() {
  late Directory tmp;
  setUp(() => tmp = Directory.systemTemp.createTempSync('mc'));
  tearDown(() => tmp.deleteSync(recursive: true));

  ControlRequest req(String from, String host, DateTime t, {String key = 'ups.alarm', String value = 'off'}) => ControlRequest(
      id: ControlRequest.newId(), from: from, fromName: from.toUpperCase(), host: host, target: 'ups1', key: key, value: value, time: t);

  test('a request is written in the asking device\'s folder and found by its host', () {
    final f = SyncFolder(tmp.path)..prepare('phone')..prepare('desk');
    final now = DateTime.utc(2026, 11, 27, 12);
    final r = req('phone', 'desk', now);
    f.sendRequest(r);
    expect(File('${f.deviceDir('phone')}/requests/${r.id}.json').existsSync(), isTrue);
    final got = f.requestsFor('desk', now.add(const Duration(minutes: 1)));
    expect(got.single.id, r.id);
    expect(got.single.fromName, 'PHONE');
    expect(got.single.value, 'off');
    expect(f.requestsFor('phone', now), isEmpty);
    expect(f.myRequests('phone').single.id, r.id);
  });

  test('old requests are ignored, newest first otherwise', () {
    final f = SyncFolder(tmp.path)..prepare('a')..prepare('b')..prepare('desk');
    final now = DateTime.utc(2026, 11, 27, 12);
    f.sendRequest(req('a', 'desk', now.subtract(const Duration(minutes: 11))));
    final older = req('a', 'desk', now.subtract(const Duration(minutes: 2)));
    final newer = req('b', 'desk', now.subtract(const Duration(minutes: 1)));
    f.sendRequest(older);
    f.sendRequest(newer);
    expect(f.requestsFor('desk', now).map((r) => r.id), [newer.id, older.id]);
  });

  test('a result marks the request handled and is read back', () {
    final f = SyncFolder(tmp.path)..prepare('phone')..prepare('desk');
    final now = DateTime.utc(2026, 11, 27, 12);
    final r = req('phone', 'desk', now);
    f.sendRequest(r);
    expect(f.handled('desk', r.id), isFalse);
    expect(f.resultOf('desk', r.id), isNull);
    f.writeResult('desk', ControlResult(id: r.id, ok: true, message: 'alarm off', value: 'off', time: now));
    expect(f.handled('desk', r.id), isTrue);
    final res = f.resultOf('desk', r.id)!;
    expect(res.ok, isTrue);
    expect(res.value, 'off');
  });

  test('conflict copies and junk are skipped', () {
    final f = SyncFolder(tmp.path)..prepare('phone')..prepare('desk');
    final now = DateTime.now().toUtc();
    final dir = Directory('${f.deviceDir('phone')}/requests')..createSync(recursive: true);
    File('${dir.path}/x.sync-conflict-20261127.json').writeAsStringSync(req('phone', 'desk', now).toJson().toString());
    File('${dir.path}/y.json').writeAsStringSync('{not json');
    File('${dir.path}/z.json').writeAsStringSync('{"id": 3}');
    expect(f.requestsFor('desk', now), isEmpty);
  });

  test('controls round-trip through a report', () {
    final c = Control(key: 'ups.alarm', label: 'Alarm', options: ['on', 'off', 'quiet'], value: 'on', confirm: 'sure?');
    final back = Control.tryParse(c.toJson())!;
    expect(back.options, ['on', 'off', 'quiet']);
    expect(back.value, 'on');
    expect(back.confirm, 'sure?');
    expect(Control.tryParse({'key': 'x'}), isNull);
  });
}
