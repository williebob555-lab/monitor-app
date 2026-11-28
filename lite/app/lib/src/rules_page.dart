import 'dart:convert';

import 'package:flutter/foundation.dart';
import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';

import 'rule_form.dart';
import 'theme.dart';
import 'widgets.dart';

class RulesPage extends StatefulWidget {
  const RulesPage(this.root, this.devices, this.things, {super.key});

  final String root;
  final Map<String, String> devices;
  final Map<String, String> things;

  @override
  State<RulesPage> createState() => _RulesPageState();
}

class _RulesPageState extends State<RulesPage> {
  late var _rules = compute(RuleSet.load, widget.root);
  bool _saving = false;

  void _reload() => setState(() {
    _rules = compute(RuleSet.load, widget.root);
  });

  Future<void> _add() async {
    final saved = await Navigator.of(context).push<bool>(
      MaterialPageRoute(
        builder: (_) => RuleForm(widget.root, widget.devices, widget.things),
      ),
    );
    if (saved == true && mounted) _reload();
  }

  Future<void> _setEnabled(Rule rule, bool enabled) async {
    setState(() => _saving = true);
    try {
      if (enabled) {
        final proceed = await showDialog<bool>(
          context: context,
          builder: (context) => AlertDialog(
            title: const Text('Enable this rule?'),
            content: Text('${rule.name}\n${_actions(rule)}'),
            actions: [
              TextButton(
                onPressed: () => Navigator.pop(context),
                child: const Text('Cancel'),
              ),
              FilledButton(
                onPressed: () => Navigator.pop(context, true),
                child: const Text('Enable'),
              ),
            ],
          ),
        );
        if (!mounted || proceed != true) return;
      }
      final latest = RuleSet.load(widget.root).rules
          .where((r) => r.id == rule.id)
          .firstOrNull;
      if (latest == null ||
          jsonEncode(latest.toJson()) != jsonEncode(rule.toJson())) {
        ScaffoldMessenger.of(context).showSnackBar(
          const SnackBar(
            content: Text('This rule changed. Review the current version.'),
          ),
        );
      } else {
        RuleSet.save(
          widget.root,
          Rule(
            id: latest.id,
            name: latest.name,
            when: latest.when,
            scope: latest.scope,
            then: latest.then,
            cooldown: latest.cooldown,
            enabled: enabled,
            extra: latest.extra,
          ),
        );
      }
      _reload();
    } on Exception {
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          const SnackBar(content: Text('Could not save this rule.')),
        );
      }
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  Future<void> _open(Rule rule) async {
    final names = {...widget.devices, ...widget.things};
    final future = compute(_readFirings, (widget.root, rule.id));
    final changed = await Navigator.of(context).push<bool>(
      MaterialPageRoute(
        builder: (context) => _RuleDetails(
          rule,
          names,
          future,
          onEdit: () async {
            final saved = await Navigator.of(context).push<bool>(
              MaterialPageRoute(
                builder: (_) => RuleForm(
                  widget.root,
                  widget.devices,
                  widget.things,
                  existing: rule,
                ),
              ),
            );
            if (context.mounted && saved == true) Navigator.pop(context, true);
          },
        ),
      ),
    );
    if (mounted && changed == true) _reload();
  }

  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppBar(
      title: const Text('Rules'),
      actions: [
        IconButton(
          tooltip: 'Reload rules',
          onPressed: _saving ? null : _reload,
          icon: const Icon(Icons.refresh),
        ),
      ],
    ),
    body: FutureBuilder<RuleSet>(
      future: _rules,
      builder: (context, snapshot) {
        final rules = snapshot.data;
        return Center(
          child: ConstrainedBox(
            constraints: const BoxConstraints(maxWidth: 760),
            child: ListView(
              padding: const EdgeInsets.fromLTRB(14, 8, 14, 24),
              children: [
                Align(
                  alignment: Alignment.centerLeft,
                  child: FilledButton.icon(
                    onPressed: _saving ? null : _add,
                    icon: const Icon(Icons.add),
                    label: const Text('New rule'),
                  ),
                ),
                const SizedBox(height: 12),
                if (snapshot.hasError)
                  const Text('Could not read rules from the Monitor folder.')
                else if (rules == null)
                  const Center(child: CircularProgressIndicator())
                else ...[
                  if (rules.rules.isEmpty) const Text('No saved rules.'),
                  for (final rule in rules.rules)
                    Card(
                      child: ListTile(
                        title: Text(rule.name),
                        subtitle: Text(_trigger(rule)),
                        onTap: () => _open(rule),
                        trailing: Semantics(
                          label: 'Enable ${rule.name}',
                          child: Switch(
                            value: rule.enabled,
                            onChanged: _saving
                                ? null
                                : (enabled) => _setEnabled(rule, enabled),
                          ),
                        ),
                      ),
                    ),
                  if (rules.problems.isNotEmpty)
                    Section(
                      'Rules that need attention',
                      Column(
                        crossAxisAlignment: CrossAxisAlignment.start,
                        children: [
                          for (final problem in rules.problems.entries) ...[
                            Text(problem.key),
                            for (final reason in problem.value)
                              Text(
                                reason,
                                style: const TextStyle(color: C.muted),
                              ),
                          ],
                        ],
                      ),
                    ),
                ],
              ],
            ),
          ),
        );
      },
    ),
  );
}

List<Map<String, Object?>> _readFirings((String, String) query) =>
    firedLines(query.$1, query.$2).reversed.take(100).toList();

String _trigger(Rule rule) => switch (rule.trigger) {
  'offline' => 'Stops reporting for ${_duration(rule.when['for'] ?? 600)}',
  'event' => 'Event · ${rule.when['event']}',
  'unusual' => 'Unusual reading reported',
  'value' =>
    '${rule.when['path']} ${rule.when['op']} ${rule.when['value']}'
        '${switch (rule.when['for']) {
          final num hold when hold > 0 => ' for ${_duration(hold)}',
          _ => '',
        }}',
  _ => rule.trigger,
};

String _duration(Object? seconds) => switch (seconds) {
  final num length when length % 60 == 0 => '${length ~/ 60} min',
  _ => '$seconds s',
};

String _actions(Rule rule) => rule.then
    .map(
      (action) => switch (action['type']) {
        'notify' => 'Send a notification',
        'annotate' => 'Add a history note',
        'control' => 'Send a settings request · ${action['key']}',
        _ => '${action['type']}',
      },
    )
    .join('\n');

class _RuleDetails extends StatelessWidget {
  const _RuleDetails(
    this.rule,
    this.names,
    this.firings, {
    required this.onEdit,
  });

  final Rule rule;
  final Map<String, String> names;
  final Future<List<Map<String, Object?>>> firings;
  final VoidCallback onEdit;

  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppBar(
      title: Text(rule.name),
      actions: [
        IconButton(
          tooltip: 'Edit rule',
          onPressed: onEdit,
          icon: const Icon(Icons.edit_outlined),
        ),
      ],
    ),
    body: ListView(
      padding: const EdgeInsets.all(14),
      children: [
        Section(
          'When it runs',
          Column(
            crossAxisAlignment: CrossAxisAlignment.start,
            children: [
              Text(_trigger(rule)),
              Text(_actions(rule)),
              if (rule.scope.values.expand((ids) => ids).isNotEmpty)
                Text(
                  rule.scope.values
                      .expand((ids) => ids)
                      .map((id) => names[id] ?? id)
                      .join(', '),
                ),
            ],
          ),
        ),
        const SizedBox(height: 12),
        FutureBuilder<List<Map<String, Object?>>>(
          future: firings,
          builder: (context, snapshot) {
            final lines = snapshot.data;
            return Section(
              'Why it fired',
              snapshot.hasError
                  ? const Text('Could not read rule activity.')
                  : lines == null
                  ? const Center(child: CircularProgressIndicator())
                  : lines.isEmpty
                  ? const Text('No firings recorded for this rule.')
                  : Column(
                      children: [
                        for (final line in lines)
                          ListTile(
                            title: Text(
                              '${line['why'] ?? 'Reason unavailable'}',
                            ),
                            subtitle: Text(
                              [
                                if (line['t'] case final num timestamp
                                    when timestamp.abs() <= 8640000000000)
                                  _date(context, timestamp.toInt()),
                                if (line['subject'] case final String id)
                                  names[id] ?? id,
                                if (line['by'] case final String id)
                                  'Evaluated by ${names[id] ?? id}',
                              ].join(' · '),
                            ),
                          ),
                      ],
                    ),
            );
          },
        ),
      ],
    ),
  );
}

String _date(BuildContext context, int seconds) {
  final date = DateTime.fromMillisecondsSinceEpoch(
    seconds * 1000,
    isUtc: true,
  ).toLocal();
  final locale = MaterialLocalizations.of(context);
  return '${locale.formatShortDate(date)} ${locale.formatTimeOfDay(TimeOfDay.fromDateTime(date))}';
}
