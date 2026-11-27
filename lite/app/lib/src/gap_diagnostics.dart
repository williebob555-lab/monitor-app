import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';

import 'theme.dart';

class GapDiagnostics extends StatefulWidget {
  const GapDiagnostics({super.key, required this.read});

  final Future<List<Gap>> Function() read;

  @override
  State<GapDiagnostics> createState() => _GapDiagnosticsState();
}

class _GapDiagnosticsState extends State<GapDiagnostics> {
  Future<List<Gap>>? _diagnosis;

  void _read() {
    final pending = widget.read();
    setState(() {
      _diagnosis = pending;
    });
  }

  @override
  Widget build(BuildContext context) => Column(
    crossAxisAlignment: CrossAxisAlignment.start,
    children: [
      if (_diagnosis == null)
        TextButton.icon(
          onPressed: _read,
          icon: const Icon(Icons.timeline, size: 18),
          label: const Text('Explain collection gaps'),
        )
      else
        FutureBuilder<List<Gap>>(
          future: _diagnosis,
          builder: (context, result) {
            if (result.hasError) {
              return TextButton(
                onPressed: _read,
                child: const Text('Could not read the gap evidence. Try again'),
              );
            }
            final gaps = result.data;
            if (gaps == null) {
              return const Text(
                'Reading collection history…',
                style: TextStyle(color: C.muted),
              );
            }
            if (gaps.isEmpty) {
              return const Text(
                'No collection gaps identified in the available detail.',
                style: TextStyle(color: C.muted),
              );
            }
            final locale = MaterialLocalizations.of(context);
            String timestamp(DateTime time) {
              final local = time.toLocal();
              return '${locale.formatShortDate(local)} ${locale.formatTimeOfDay(TimeOfDay.fromDateTime(local))}';
            }

            return Column(
              crossAxisAlignment: CrossAxisAlignment.start,
              children: [
                Text(
                  'Collection gaps · ${gaps.length}',
                  style: const TextStyle(fontWeight: FontWeight.w600),
                ),
                for (final gap in gaps.reversed)
                  Padding(
                    padding: const EdgeInsets.only(top: 6),
                    child: Text(
                      '${timestamp(gap.start)} – ${timestamp(gap.end)}\n${_description(gap.why)}',
                      style: TextStyle(
                        fontSize: 12,
                        color: gap.why == 'collector' ? C.warn : C.muted,
                      ),
                    ),
                  ),
              ],
            );
          },
        ),
    ],
  );
}

String _description(String why) => switch (why) {
  'collector' => 'Device was reachable during the gap',
  'off' => 'Device appears to have been off or unreachable',
  'asleep' => 'Android may have delayed collection while asleep',
  _ => 'Cause unknown',
};
