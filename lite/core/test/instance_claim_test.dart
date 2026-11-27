import 'dart:io';

import 'package:monitor_core/agent.dart';
import 'package:test/test.dart';

void main() {
  test('the newest copy of the collector holds the claim; older ones see they lost it', () {
    final dir = Directory.systemTemp.createTempSync('claim');
    addTearDown(() => dir.deleteSync(recursive: true));
    final path = '${dir.path}/agent.owner';
    final old = InstanceClaim(path, pid: 10, started: DateTime(2026, 11, 26, 19))..take();
    expect(old.held(), isTrue);
    final newer = InstanceClaim(path, pid: 11, started: DateTime(2026, 11, 26, 20))..take();
    expect(newer.held(), isTrue);
    expect(old.held(), isFalse);
  });

  test('a missing, empty or unreadable claim never stops a copy', () {
    final dir = Directory.systemTemp.createTempSync('claim');
    addTearDown(() => dir.deleteSync(recursive: true));
    final c = InstanceClaim('${dir.path}/agent.owner', pid: 1);
    expect(c.held(), isTrue);
    File('${dir.path}/agent.owner').writeAsStringSync('');
    expect(c.held(), isTrue);
    expect(InstanceClaim('${dir.path}/agent.owner/x', pid: 2).held(), isTrue);
  });
}
