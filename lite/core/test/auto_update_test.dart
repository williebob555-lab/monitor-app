import 'dart:convert';
import 'dart:io';

import 'package:monitor_core/monitor_core.dart';
import 'package:test/test.dart';

void main() {
  late Directory d;
  setUp(() => d = Directory.systemTemp.createTempSync('auto-update'));
  tearDown(() => d.deleteSync(recursive: true));

  test('automatic updates are on unless settings.json says false', () {
    expect(autoUpdateOn(d.path), isTrue);
    File('${d.path}/settings.json').writeAsStringSync(jsonEncode({'history': {'maxMb': 50}}));
    expect(autoUpdateOn(d.path), isTrue);
    File('${d.path}/settings.json').writeAsStringSync(jsonEncode({'update': {'auto': false}}));
    expect(autoUpdateOn(d.path), isFalse);
    File('${d.path}/settings.json').writeAsStringSync('not json');
    expect(autoUpdateOn(d.path), isTrue);
  });

  test('watchedHere is true only for this device while its watch file is fresh', () {
    final f = SyncFolder(d.path);
    final now = DateTime.utc(2026, 11, 27, 12);
    expect(f.watchedHere('aa', now), isFalse);
    f.writeWatch('aa', 'laptop', now);
    expect(f.watchedHere('aa', now), isTrue);
    expect(f.watchedHere('bb', now), isFalse);
    expect(f.watchedHere('aa', now.add(const Duration(seconds: watchHoldSeconds + 1))), isFalse);
  });
}
