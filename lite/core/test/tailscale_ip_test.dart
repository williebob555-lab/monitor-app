import 'package:monitor_core/src/agent/agent.dart';
import 'package:test/test.dart';

void main() {
  test("the Pixel's carrier CGNAT address is not Tailscale (2026-11-27)", () {
    expect(pickTailscale([('rmnet16', '100.86.5.58'), ('tun0', '100.88.178.60')]), '100.88.178.60');
  });
  test('tailscale0 on a computer', () {
    expect(pickTailscale([('tailscale0', '100.101.108.97')]), '100.101.108.97');
  });
  test('only a cellular 100.64/10 address: none', () {
    expect(pickTailscale([('rmnet16', '100.86.5.58')]), isNull);
  });
}
