import 'package:flutter/foundation.dart';
import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';

import 'state.dart';
import 'theme.dart';
import 'widgets.dart';

bool canSummarize(Iterable<Report> reports, DateTime now) => reports.any(
  (report) =>
      !report.isThing &&
      report.kind != 'android' &&
      now.difference(report.time) <= freshLimit(report) &&
      modelsOf(report).isNotEmpty,
);

String _requestSummary((String, String, bool) query) =>
    requestAi(query.$1, query.$2, 'week-summary', small: query.$3);

Map<String, Object?>? _readSummary((String, String) query) =>
    aiAnswer(query.$1, query.$2);

class SummaryPage extends StatefulWidget {
  const SummaryPage(this.state, {super.key});
  final AppState state;

  @override
  State<SummaryPage> createState() => _SummaryPageState();
}

class _SummaryPageState extends State<SummaryPage> {
  late final _root = widget.state.folder.root;
  late String? _id = widget.state.summaryRequests[_root]?.$1;
  late DateTime? _requestedAt = widget.state.summaryRequests[_root]?.$2;
  Map<String, Object?>? _answer;
  DateTime? _lastRead;
  bool _submitting = false;
  bool _reading = false;
  bool _small = false;
  bool _failed = false;
  bool _late = false;

  @override
  void initState() {
    super.initState();
    widget.state.addListener(_update);
    WidgetsBinding.instance.addPostFrameCallback((_) => _update());
  }

  @override
  void dispose() {
    widget.state.removeListener(_update);
    super.dispose();
  }

  Future<void> _update() async {
    final id = _id;
    final now = DateTime.now();
    if (!mounted ||
        id == null ||
        _answer != null ||
        _reading ||
        ModalRoute.isCurrentOf(context) != true ||
        (_lastRead != null &&
            now.difference(_lastRead!) < const Duration(seconds: 3))) {
      return;
    }
    if (!_late &&
        _requestedAt != null &&
        now.difference(_requestedAt!) >= const Duration(minutes: 2)) {
      setState(() => _late = true);
    }
    _reading = true;
    _lastRead = now;
    try {
      final answer = await compute(_readSummary, (_root, id));
      if (mounted && id == _id && answer != null) {
        setState(() => _answer = answer);
      }
    } on Exception {
      if (mounted && id == _id) setState(() => _failed = true);
    } finally {
      _reading = false;
    }
  }

  Future<void> _submit() async {
    final state = widget.state;
    setState(() {
      _submitting = true;
      _failed = false;
    });
    try {
      final id = await compute(_requestSummary, (_root, state.host.id, _small));
      final requestedAt = DateTime.now();
      state.summaryRequests[_root] = (id, requestedAt);
      if (!mounted) return;
      setState(() {
        _id = id;
        _requestedAt = requestedAt;
        _answer = null;
        _lastRead = null;
        _late = false;
      });
      await _update();
    } on Exception {
      if (mounted) setState(() => _failed = true);
    } finally {
      if (mounted) setState(() => _submitting = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final state = widget.state;
    final local = state.reports[state.host.id];
    final hasSmall = local != null && canSummarize([local], DateTime.now());
    final text = _answer?['text'];
    final waiting = _id != null && _answer == null;
    final available = canSummarize(state.reports.values, DateTime.now());
    return Scaffold(
      appBar: AppBar(title: const Text('Weekly summary')),
      body: Center(
        child: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 720),
          child: ListView(
            padding: const EdgeInsets.all(14),
            children: [
              const Text(
                'Summarize the last seven days of Monitor events using a model on your own computers. The result is saved in your shared Monitor folder.',
              ),
              const SizedBox(height: 12),
              if (hasSmall)
                SwitchListTile(
                  contentPadding: EdgeInsets.zero,
                  title: const Text('Use this computer’s small model'),
                  value: _small,
                  onChanged: (waiting && !_late) || _submitting
                      ? null
                      : (value) => setState(() => _small = value),
                ),
              Align(
                alignment: Alignment.centerLeft,
                child: FilledButton(
                  onPressed: _submitting || (waiting && !_late) || !available
                      ? null
                      : _submit,
                  child: Text(
                    waiting && _late
                        ? 'Request again'
                        : _answer == null
                        ? 'Request summary'
                        : 'Request a new summary',
                  ),
                ),
              ),
              if (_failed)
                const Text(
                  'Could not read or send the summary request.',
                  style: TextStyle(color: C.warn),
                ),
              if (waiting)
                Padding(
                  padding: const EdgeInsets.only(top: 12),
                  child: Text(
                    _late
                        ? 'The model is taking longer than expected. You can wait here or request another summary.'
                        : 'Waiting for a model. You can leave and come back while Monitor stays open.',
                  ),
                ),
              if (_answer != null) ...[
                const SizedBox(height: 12),
                Section(
                  'Generated summary',
                  Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      if (text is String && text.trim().isNotEmpty)
                        SelectableText(text)
                      else
                        const Text('The model did not return a summary.'),
                      if (_answer?['by'] case final String host)
                        Text(
                          'From ${state.names[host] ?? host}',
                          style: const TextStyle(color: C.muted),
                        ),
                      if (_answer?['model'] case final String model)
                        Text(model, style: const TextStyle(color: C.muted)),
                    ],
                  ),
                ),
              ],
            ],
          ),
        ),
      ),
    );
  }
}
