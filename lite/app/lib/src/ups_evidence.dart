import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';

import 'theme.dart';

class UpsEvidence extends StatelessWidget {
  const UpsEvidence(this.report, {super.key});

  final Report report;

  @override
  Widget build(BuildContext context) {
    final readings = {
      for (final reading in report.readings) reading.key: reading,
    };
    const style = TextStyle(color: C.muted);
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        if (readings['ups.source']?.value case final String source
            when source.isNotEmpty)
          Text('Read through $source', style: style),
        if (readings['ups.hostAction']?.value case final String action
            when action.isNotEmpty)
          Text('Low battery policy · $action (declared)', style: style),
        if (readings['ups.identity'] case Reading(
          value: final String identity,
          :final note,
        ))
          Text([identity, ?note].join(' · '), style: style),
        if (readings['ups.load'] case Reading(
          value: null,
          note: final String reason,
        ))
          Text('Load unavailable · $reason', style: style),
      ],
    );
  }
}
