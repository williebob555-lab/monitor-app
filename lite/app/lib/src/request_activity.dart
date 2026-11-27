import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';

import 'theme.dart';

/// Status of existing requests sent by this install. The host's answer is
/// shown verbatim because it may be an acknowledgement rather than completion.
class RequestActivity extends StatelessWidget {
  const RequestActivity({
    super.key,
    required this.requests,
    required this.answerFor,
    required this.names,
    required this.now,
  });

  final Iterable<ControlRequest> requests;
  final ControlResult? Function(ControlRequest) answerFor;
  final Map<String, String> names;
  final DateTime now;

  @override
  Widget build(BuildContext context) {
    final ordered =
        requests
            .where(
              (request) =>
                  now.difference(request.time) <= const Duration(days: 1),
            )
            .toList()
          ..sort((a, b) => b.time.compareTo(a.time));
    final waiting = <Widget>[];
    final recent = <Widget>[];
    for (final request in ordered) {
      final answer = answerFor(request);
      if (answer == null &&
          now.difference(request.time).abs() <= ControlRequest.maxAge) {
        waiting.add(_row(context, request, 'Waiting', C.warn));
      } else if (answer == null) {
        recent.add(_row(context, request, 'Expired', C.muted));
      } else {
        recent.add(
          _row(
            context,
            request,
            answer.ok ? 'Answered' : 'Failed',
            answer.ok ? C.muted : C.fault,
            message: answer.message,
          ),
        );
      }
    }

    return Scaffold(
      appBar: AppBar(title: const Text('Settings requests')),
      body: Center(
        child: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 760),
          child: ListView(
            padding: const EdgeInsets.fromLTRB(16, 14, 16, 26),
            children: [
              const Text(
                'Requests sent from this device in the last 24 hours.',
                style: TextStyle(color: C.muted),
              ),
              const SizedBox(height: 14),
              Text(
                'Waiting for a response',
                style: Theme.of(context).textTheme.titleMedium,
              ),
              if (waiting.isEmpty)
                const Padding(
                  padding: EdgeInsets.symmetric(vertical: 8),
                  child: Text('Nothing is waiting.'),
                )
              else
                ...waiting,
              const SizedBox(height: 14),
              if (recent.isNotEmpty) ...[
                Text(
                  'Recent results',
                  style: Theme.of(context).textTheme.titleMedium,
                ),
                ...recent,
              ] else if (waiting.isEmpty)
                const Text(
                  'No recent requests.',
                  style: TextStyle(color: C.muted),
                ),
            ],
          ),
        ),
      ),
    );
  }

  Widget _row(
    BuildContext context,
    ControlRequest request,
    String status,
    Color color, {
    String? message,
  }) {
    final target = names[request.target] ?? request.target;
    final operation = switch (request.key) {
      'update' => 'Update',
      'ups.alarm' => 'UPS alarm · ${request.value}',
      _ => '${request.key} · ${request.value}',
    };
    final local = request.time.toLocal();
    final locale = MaterialLocalizations.of(context);
    final timestamp =
        '${locale.formatShortDate(local)} ${locale.formatTimeOfDay(TimeOfDay.fromDateTime(local))}';
    return Padding(
      padding: const EdgeInsets.only(top: 8),
      child: Card(
        child: Padding(
          padding: const EdgeInsets.all(12),
          child: Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(
                '$operation · $target',
                style: const TextStyle(fontWeight: FontWeight.w600),
              ),
              const SizedBox(height: 4),
              Text('$status · $timestamp', style: TextStyle(color: color)),
              if (message != null && message.isNotEmpty)
                Padding(
                  padding: const EdgeInsets.only(top: 4),
                  child: Text(message),
                ),
            ],
          ),
        ),
      ),
    );
  }
}
