import 'package:monitor_core/agent.dart';
import 'package:test/test.dart';

void main() {
  test('the collector restarts once with a small young-object space', () {
    expect(vmOptionsToAdd({}, '/usr/bin/monitor-agent'), vmOptions);
    expect(vmOptionsToAdd({'DART_VM_OPTIONS': '--foo'}, r'C:\Program Files\Monitor App\monitor-agent.exe'), '--foo,$vmOptions');
    // Already set (the restarted copy), switched off, or under `dart run`.
    expect(vmOptionsToAdd({'DART_VM_OPTIONS': vmOptions}, '/usr/bin/monitor-agent'), isNull);
    expect(vmOptionsToAdd({'MONITOR_VM_OPTIONS': 'off'}, '/usr/bin/monitor-agent'), isNull);
    expect(vmOptionsToAdd({}, '/opt/dart-sdk/bin/dart'), isNull);
  });
}
