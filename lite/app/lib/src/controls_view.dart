import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';

import 'state.dart';
import 'theme.dart';
import 'widgets.dart';

/// The settings a device or thing offers to change (its "controls"), with
/// what was last asked and what the owning device answered.
///
/// Any device in the shared folder can ask; the owning device applies the
/// change on its next check, writes its answer, and logs who asked.
class ControlsSection extends StatelessWidget {
  const ControlsSection(this.state, this.r, {super.key});
  final AppState state;
  final Report r;

  @override
  Widget build(BuildContext context) {
    final rows = <Widget>[];
    for (final c in r.controls) {
      if (rows.isNotEmpty) rows.add(const Divider(height: 24));
      rows.add(_ControlRow(state, r, c));
    }
    return Section('Change from here', Column(crossAxisAlignment: CrossAxisAlignment.start, children: rows));
  }
}

String actionLabel(String option) => option == 'update' ? 'Update now' : '${option[0].toUpperCase()}${option.substring(1)}';

/// Asks first, in the app (no fingerprint or PIN needed), then sends.
Future<void> confirmAsk(BuildContext context, AppState st, Report r, Control c, String value) async {
  final owner = st.things[r.id]?.host ?? r.id;
  final ownerName = st.names[owner] ?? owner;
  final action = c.options.length == 1;
  final ok = await showDialog<bool>(
    context: context,
    builder: (context) => AlertDialog(
      title: Text(action ? '${actionLabel(value)}: ${r.name}?' : 'Set ${c.label} on ${r.name} to $value?'),
      content: Text([
        if (c.confirm != null) c.confirm!,
        '$ownerName applies it the next time it checks, and its log notes that ${st.myName} asked.',
      ].join('\n\n')),
      actions: [
        TextButton(onPressed: () => Navigator.of(context).pop(false), child: const Text('Cancel')),
        FilledButton(onPressed: () => Navigator.of(context).pop(true), child: Text(action ? actionLabel(value) : 'Set $value')),
      ],
    ),
  );
  if (ok == true) st.ask(r.id, c.key, value);
}

class _ControlRow extends StatelessWidget {
  const _ControlRow(this.st, this.r, this.c);
  final AppState st;
  final Report r;
  final Control c;

  @override
  Widget build(BuildContext context) {
    final last = st.lastAsk(r.id, c.key);
    final action = c.options.length == 1;
    final waiting = last != null && last.$2 == null && DateTime.now().toUtc().difference(last.$1.time) < ControlRequest.maxAge;
    return Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
      Row(children: [
        Expanded(child: Text(c.label, style: const TextStyle(fontWeight: FontWeight.w600))),
        if (c.value != null && action) Text(c.key == 'update' ? 'version ${c.value}' : c.value!, style: const TextStyle(color: C.muted, fontSize: 12.5)),
      ]),
      if (c.note != null) ...[
        const SizedBox(height: 4),
        Text(c.note!, style: const TextStyle(color: C.muted, fontSize: 12, height: 1.4)),
      ],
      const SizedBox(height: 10),
      if (action)
        FilledButton.tonalIcon(
          onPressed: waiting ? null : () => confirmAsk(context, st, r, c, c.options.single),
          icon: Icon(c.key == 'update' ? Icons.system_update_alt : Icons.play_arrow, size: 18),
          label: Text(actionLabel(c.options.single)),
        )
      else
        Wrap(spacing: 8, runSpacing: 8, children: [
          for (final o in c.options)
            ChoiceChip(
              label: Text(o),
              selected: o == c.value,
              onSelected: waiting ? null : (_) => o == c.value ? null : confirmAsk(context, st, r, c, o),
            ),
        ]),
      if (last != null) ...[
        const SizedBox(height: 8),
        Text(askStatus(st, last.$1, last.$2), style: TextStyle(color: last.$2?.ok == false ? C.fault : C.muted, fontSize: 12, height: 1.4)),
      ],
    ]);
  }
}

String _hm(DateTime t) {
  final l = t.toLocal();
  return '${l.hour.toString().padLeft(2, '0')}:${l.minute.toString().padLeft(2, '0')}';
}

/// One line on where a request stands, in words.
String askStatus(AppState st, ControlRequest q, ControlResult? a) {
  final who = st.names[q.host] ?? q.host;
  final what = q.key == 'update' ? actionLabel(q.value) : q.value;
  if (a != null) {
    return a.ok ? '$who did it at ${_hm(a.time)}: ${a.message}.' : '$who could not ($what, asked ${_hm(q.time)}): ${a.message}.';
  }
  if (DateTime.now().toUtc().difference(q.time) >= ControlRequest.maxAge) {
    return '$who did not answer within ${ControlRequest.maxAge.inMinutes} minutes, so the request ($what, ${_hm(q.time)}) lapsed. It may be off or not synced.';
  }
  final every = st.reports[q.host]?.nextSeconds;
  final pace = every == null ? '' : ' (it checks about every ${every >= 90 ? '${(every / 60).round()} min' : '$every s'})';
  return 'Asked ${_hm(q.time)}: waiting for $who to pick it up$pace.';
}
