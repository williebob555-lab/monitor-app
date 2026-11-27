import 'dart:convert';
import 'dart:io';

import 'package:monitor_core/monitor_core.dart';
import 'package:test/test.dart';

void main() {
  late Directory root;
  setUp(() => root = Directory.systemTemp.createTempSync('ai'));
  tearDown(() => root.deleteSync(recursive: true));
  final now = DateTime.utc(2026, 11, 27, 22);
  Report pc(String id, String name, {String? models, int ageS = 10, String kind = 'linux'}) => Report(
      id: id, name: name, kind: kind, time: now.subtract(Duration(seconds: ageS)), nextSeconds: 60,
      readings: [if (models != null) Reading(key: 'ai.models', value: models)]);

  test('alpha answers when up; beta when alpha is down; none without models', () {
    final alpha = pc('d8fea3c0a082', 'wc-alpha', models: 'llama3.1:8b');
    final beta = pc('ebad35097c54', 'wc-beta', models: 'qwen2.5:3b');
    expect(aiHost(root.path, {alpha.id: alpha, beta.id: beta}, now), alpha.id);
    final down = pc('d8fea3c0a082', 'wc-alpha', models: 'llama3.1:8b', ageS: 3600);
    expect(aiHost(root.path, {down.id: down, beta.id: beta}, now), beta.id);
    expect(aiHost(root.path, {'x': pc('x', 'laptop')}, now), isNull);
  });

  test('biggest model for the host, smallest on demand', () {
    expect(pickModel(['qwen2.5:3b', 'llama3.1:70b', 'llama3.1:8b']), 'llama3.1:70b');
    expect(pickModel(['qwen2.5:3b', 'llama3.1:8b'], small: true), 'qwen2.5:3b');
  });

  test('a week summary request is answered once, by the host, from the events', () async {
    final alpha = pc('d8fea3c0a082', 'wc-alpha', models: 'llama3.1:8b');
    final laptop = pc('301230114386', 'laptop');
    final reports = {alpha.id: alpha, laptop.id: laptop};
    final id = requestAi(root.path, laptop.id, 'week-summary', now: now);
    String? prompt;
    Future<(int, String)> fake(String m, Uri u, Map<String, String> h, String? b) async {
      prompt = (jsonDecode(b!) as Map)['prompt'] as String;
      return (200, jsonEncode({'response': ' All quiet. '}));
    }

    expect(await answerAiRequests(root.path, laptop.id, reports, const [], now, http: fake), isEmpty);
    final log = await answerAiRequests(root.path, alpha.id, reports, ['llama3.1:8b'], now, http: fake);
    expect(log.single, contains('week-summary'));
    expect(prompt, contains('last 7 days'));
    expect(aiAnswer(root.path, id)!['text'], 'All quiet.');
    expect(await answerAiRequests(root.path, alpha.id, reports, ['llama3.1:8b'], now, http: fake), isEmpty);
  });

  test('small on demand: the requester answers itself', () async {
    final alpha = pc('d8fea3c0a082', 'wc-alpha', models: 'llama3.1:8b');
    final laptop = pc('301230114386', 'laptop', models: 'qwen2.5:3b');
    final reports = {alpha.id: alpha, laptop.id: laptop};
    final id = requestAi(root.path, laptop.id, 'explain', text: 'physical: a USB-C plug does not go into a USB-A', small: true, now: now);
    Future<(int, String)> fake(String m, Uri u, Map<String, String> h, String? b) async => (200, jsonEncode({'response': 'ok'}));
    expect(await answerAiRequests(root.path, alpha.id, reports, ['llama3.1:8b'], now, http: fake), isEmpty);
    await answerAiRequests(root.path, laptop.id, reports, ['qwen2.5:3b'], now, http: fake);
    expect(aiAnswer(root.path, id)!['model'], 'qwen2.5:3b');
  });
}
