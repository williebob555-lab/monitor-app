import 'package:monitor_core/src/agent/extras.dart';
import 'package:test/test.dart';

void main() {
  test('no Docker on the host: nothing said', () {
    expect(Extras.containersUnseen(dockerHere: false, dockerHost: '', socket: false), isNull);
  });
  test('Docker but no helper set up (wc-beta 2026-11-27): says how', () {
    expect(Extras.containersUnseen(dockerHere: true, dockerHost: '', socket: false), contains('docker-list helper is not set up'));
  });
  test('helper configured but down: names it', () {
    expect(Extras.containersUnseen(dockerHere: true, dockerHost: 'tcp://127.0.0.1:2375', socket: false), contains('127.0.0.1:2375 does not answer'));
  });
}
