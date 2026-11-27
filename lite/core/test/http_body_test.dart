import 'dart:convert';

import 'package:monitor_core/src/agent/extras.dart' show httpBody;
import 'package:test/test.dart';

void main() {
  test('plain and chunked bodies; non-200 refused', () {
    expect(httpBody(utf8.encode('HTTP/1.1 200 OK\r\nContent-Type: application/json\r\n\r\n{"a":1}')), '{"a":1}');
    expect(httpBody(utf8.encode('HTTP/1.1 200 OK\r\nTransfer-Encoding: chunked\r\n\r\n4\r\n{"a"\r\n3\r\n:1}\r\n0\r\n\r\n')), '{"a":1}');
    expect(() => httpBody(utf8.encode('HTTP/1.1 403 Forbidden\r\n\r\nno')), throwsFormatException);
  });
}
