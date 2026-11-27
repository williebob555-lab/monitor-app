import 'package:monitor_core/monitor_core.dart';
import 'package:monitor_core/src/agent/smart.dart';
import 'package:test/test.dart';

// Shaped like `smartctl -j -i -H -A` (smartmontools 7.x), trimmed.
final nvme = {
  'smartctl': {'exit_status': 0},
  'device': {'name': '/dev/nvme0', 'type': 'nvme'},
  'model_name': 'Samsung SSD 980 1TB',
  'serial_number': 'S64ANS0T123456X',
  'user_capacity': {'bytes': 1000204886016},
  'smart_status': {'passed': true},
  'temperature': {'current': 38},
  'power_on_time': {'hours': 5321},
  'nvme_smart_health_information_log': {'percentage_used': 3, 'media_errors': 0},
};
final sata = {
  'smartctl': {'exit_status': 0},
  'model_name': 'ST2000DM008-2FR102',
  'serial_number': 'ZFL1ABCD',
  'rotation_rate': 7200,
  'smart_status': {'passed': true},
  'temperature': {'current': 41},
  'power_on_time': {'hours': 20111},
  'ata_smart_attributes': {
    'table': [
      {'id': 5, 'value': 100, 'raw': {'value': 8}},
      {'id': 197, 'value': 100, 'raw': {'value': 0}},
      {'id': 198, 'value': 100, 'raw': {'value': 0}},
    ]
  },
};

void main() {
  final now = DateTime.utc(2026, 11, 27, 21);
  final t = now.millisecondsSinceEpoch ~/ 1000 - 600;
  String? v(Report r, String k) => r.readings.where((x) => x.key == k).firstOrNull?.value?.toString();

  test('NVMe: health, wear, hours; id from model + serial (same on both systems)', () {
    final a = Smart.diskReport({'exported': t, 'device': '/dev/nvme0', 'smart': nvme}, 'alpha', 'm', now, 60)!;
    final b = Smart.diskReport({'exported': t, 'device': '/dev/nvme0', 'smart': nvme}, 'desktop', 'm', now, 60)!;
    expect(a.id, 'disk-samsung-ssd-980-1tb-s64ans0t123456x');
    expect(b.id, a.id);
    expect(a.node!.type, 'disk');
    expect(v(a, 'disk.health'), 'passed');
    expect(v(a, 'disk.wear'), '3');
    expect(v(a, 'disk.hours'), '5321');
    expect(v(a, 'disk.kind'), 'NVMe SSD');
  });

  test('hard disk: reallocated sectors warn', () {
    final r = Smart.diskReport({'exported': t, 'device': '/dev/sda', 'smart': sata}, 'alpha', 'm', now, 60)!;
    expect(v(r, 'disk.kind'), 'Hard disk');
    expect(r.readings.singleWhere((x) => x.key == 'disk.realloc').level, 'warn');
  });

  test('failing health is a fault', () {
    final bad = {...nvme, 'smart_status': {'passed': false}};
    final r = Smart.diskReport({'exported': t, 'device': '/dev/nvme0', 'smart': bad}, 'alpha', 'm', now, 60)!;
    expect(r.readings.singleWhere((x) => x.key == 'disk.health').level, 'fault');
  });

  test('sleeping disk is left asleep, not unknown', () {
    final r = Smart.diskReport({'exported': t, 'device': '/dev/sdb', 'smart': {'smartctl': {'exit_status': 2}}}, 'alpha', 'm', now, 60)!;
    expect(v(r, 'disk.health'), 'asleep');
  });

  test('stale or failed export: unknown with the reason, never old numbers', () {
    final old = Smart.diskReport({'exported': t - 3 * 3600, 'device': '/dev/nvme0', 'smart': nvme}, 'alpha', 'm', now, 60)!;
    final h = old.readings.singleWhere((x) => x.key == 'disk.health');
    expect(h.value, isNull);
    expect(h.src, 'unknown');
    expect(h.note, contains('min ago'));
    final err = Smart.diskReport({'exported': t, 'device': '/dev/sda', 'error': 'smartctl not installed'}, 'alpha', 'm', now, 60)!;
    expect(err.readings.first.note, 'smartctl not installed');
  });
}
