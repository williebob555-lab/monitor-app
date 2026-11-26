import 'dart:convert';
import 'dart:io';

import 'package:monitor_core/monitor_core.dart';
import 'package:test/test.dart';

void main() {
  late Directory tmp;
  setUp(() => tmp = Directory.systemTemp.createTempSync('mf'));
  tearDown(() => tmp.deleteSync(recursive: true));

  Map<String, Object?> rep(int t, int seq) => {'v': 1, 'id': 'aa', 'name': 'A', 'kind': 'android', 't': t, 'seq': seq};

  test('a changed report is read again even with the same size and file time', () {
    final f = SyncFolder(tmp.path)..prepare('aa');
    final file = File('${f.deviceDir('aa')}/now.json');
    file.writeAsStringSync(jsonEncode(rep(1000, 1)));
    final stamp = DateTime(2026, 1, 1);
    file.setLastModifiedSync(stamp);
    expect(f.readReports()['aa']!.seq, 1);
    file.writeAsStringSync(jsonEncode(rep(1001, 2))); // same length
    file.setLastModifiedSync(stamp);
    expect(f.readReports()['aa']!.seq, 2);
  });

  test('a half-written report keeps the previous one', () {
    final f = SyncFolder(tmp.path)..prepare('aa');
    final file = File('${f.deviceDir('aa')}/now.json');
    file.writeAsStringSync(jsonEncode(rep(1000, 1)));
    f.readReports();
    file.writeAsStringSync('{"v":1,"id":"aa"');
    expect(f.readReports()['aa']!.seq, 1);
  });

  test('watch files count only until they expire, and clear removes them', () {
    final f = SyncFolder(tmp.path);
    final now = DateTime.utc(2026, 11, 26, 12);
    expect(f.watchers(now), isEmpty);
    f.writeWatch('pp', 'Pixel', now);
    expect(f.watchers(now.add(const Duration(seconds: 30))), ['Pixel']);
    expect(f.watchers(now.add(const Duration(seconds: watchHoldSeconds + 1))), isEmpty);
    f.clearWatch('pp');
    expect(f.watchers(now), isEmpty);
  });

  test('the live rate and its watchers survive a round trip', () {
    final r = Report(id: 'a', name: 'A', kind: 'linux', time: DateTime.utc(2026), nextSeconds: 10, watchedBy: ['Pixel']);
    final back = Report.tryParse(jsonDecode(jsonEncode(r.toJson())))!;
    expect(back.live, isTrue);
    expect(back.watchedBy, ['Pixel']);
  });
}
