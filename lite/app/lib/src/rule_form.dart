import 'dart:convert';

import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';

import 'theme.dart';

class RuleForm extends StatefulWidget {
  const RuleForm(
    this.root,
    this.devices,
    this.things, {
    super.key,
    this.existing,
  });

  final String root;
  final Map<String, String> devices;
  final Map<String, String> things;
  final Rule? existing;

  @override
  State<RuleForm> createState() => _RuleFormState();
}

class _RuleFormState extends State<RuleForm> {
  final _form = GlobalKey<FormState>();
  final Map<String, String> _text = {};
  List<String> _problems = [];
  String _trigger = 'offline';
  String _operator = '>';
  String _valueType = 'number';
  (String, String)? _scope;
  String? _notifyDevice;
  bool _notify = true;
  bool _annotate = false;
  bool _notificationDestinationChanged = false;
  bool _saving = false;

  @override
  void initState() {
    super.initState();
    final rule = widget.existing;
    if (rule == null) return;
    _trigger = rule.trigger;
    if (rule.when['op'] case final String op
        when const {'>', '>=', '<', '<=', '==', '!='}.contains(op)) {
      _operator = op;
    }
    final value = rule.when['value'];
    _valueType = switch (value) {
      num _ => 'number',
      bool _ => 'boolean',
      _ => 'text',
    };
    _text.addAll({
      'name': rule.name,
      'cooldown': '${rule.cooldown}',
      if (rule.when['event'] case final String event) 'event': event,
      if (rule.when['path'] case final String path) 'path': path,
      if (value != null) 'value': '$value',
      if (rule.when['for'] case final num hold) 'duration': '${hold.toInt()}',
    });
    _notify = rule.then.any((action) => action['type'] == 'notify');
    _annotate = rule.then.any((action) => action['type'] == 'annotate');
  }

  Future<void> _save() async {
    if (_saving || !_form.currentState!.validate()) return;
    final original = widget.existing;
    final seconds = original == null ? 60 : 1;
    final duration =
        int.tryParse(_text['duration'] ?? '') ??
        (_trigger == 'offline' ? 600 ~/ seconds : 0);
    final cooldown = int.tryParse(_text['cooldown'] ?? '') ?? 3600 ~/ seconds;
    final value = _text['value'] ?? '';
    final number = num.tryParse(value);
    final rule = Rule(
      id: original?.id ?? newModelId(),
      name: _text['name']!,
      enabled: original?.enabled ?? false,
      extra: original?.extra,
      when: {
        if (original?.trigger == _trigger) ...original!.when,
        'type': _trigger,
        if (_trigger == 'value') ...{
          'path': _text['path'],
          'op': _operator,
          'value': switch (_valueType) {
            'number' => number?.isFinite == true ? number : null,
            'boolean' => bool.tryParse(value, caseSensitive: false),
            _ => value,
          },
        },
        if (_trigger == 'event') 'event': _text['event'],
        if (_trigger == 'offline' || _trigger == 'value')
          if (_text.containsKey('duration') || original?.trigger != _trigger)
            'for': duration * seconds,
      },
      scope: switch (_scope) {
        null => original?.scope ?? const {},
        ('', '') => const {},
        (final group, final id) => {
          group: [id],
        },
      },
      then: _actions(original?.then ?? const []),
      cooldown: cooldown * seconds,
    );
    final problems = validateRule(rule.toJson());
    if (problems.isNotEmpty) {
      setState(() => _problems = problems);
      return;
    }
    setState(() => _saving = true);
    try {
      if (original?.enabled == true) {
        final confirm = await showDialog<bool>(
          context: context,
          builder: (context) => AlertDialog(
            title: const Text('Save changes to this active rule?'),
            content: const Text(
              'The new condition and settings take effect as soon as devices read the rule.',
            ),
            actions: [
              TextButton(
                onPressed: () => Navigator.pop(context),
                child: const Text('Cancel'),
              ),
              FilledButton(
                onPressed: () => Navigator.pop(context, true),
                child: const Text('Save changes'),
              ),
            ],
          ),
        );
        if (!mounted || confirm != true) return;
      }
      if (original != null) {
        final latest = RuleSet.load(widget.root).rules
            .where((rule) => rule.id == original.id)
            .firstOrNull;
        if (latest == null ||
            jsonEncode(latest.toJson()) != jsonEncode(original.toJson())) {
          setState(
            () => _problems = [
              'The rule changed elsewhere. Reopen its latest version.',
            ],
          );
          return;
        }
      }
      RuleSet.save(widget.root, rule);
      Navigator.pop(context, true);
    } on Object {
      setState(
        () => _problems = ['Could not save the rule to the Monitor folder.'],
      );
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  List<Map<String, Object?>> _actions(List<Map<String, Object?>> recorded) {
    final actions = <Map<String, Object?>>[];
    var hasNotify = false;
    var hasAnnotate = false;
    for (final action in recorded) {
      switch (action['type']) {
        case 'notify':
          hasNotify = true;
          if (_notify) {
            final copy = {...action};
            if (_notificationDestinationChanged) {
              if (_notifyDevice case final id?) {
                copy['on'] = [id];
              } else {
                copy.remove('on');
              }
            }
            actions.add(copy);
          }
        case 'annotate':
          hasAnnotate = true;
          if (_annotate) actions.add(action);
        default:
          actions.add(action);
      }
    }
    return [
      ...actions,
      if (_notify && !hasNotify)
        {
          'type': 'notify',
          if (_notifyDevice case final id?) 'on': [id],
        },
      if (_annotate && !hasAnnotate) {'type': 'annotate'},
    ];
  }

  Widget _field(
    String key,
    String label, {
    String initial = '',
    String? example,
    bool required = false,
    int? max,
  }) => TextFormField(
    key: ValueKey(key == 'duration' ? 'duration-$_trigger' : key),
    initialValue: _text[key] ?? initial,
    decoration: InputDecoration(
      labelText: label,
      hintText: example,
      border: const OutlineInputBorder(),
    ),
    keyboardType: max != null ? TextInputType.number : TextInputType.text,
    onChanged: (value) => _text[key] = value.trim(),
    validator: (value) {
      final text = value?.trim() ?? '';
      if (required && text.isEmpty) return 'Enter a value.';
      if (max != null && text.isNotEmpty) {
        final number = int.tryParse(text);
        if (number == null || number < 0 || number > max) {
          return 'Enter a whole number from 0 to $max.';
        }
      }
      return null;
    },
  );

  Widget _picker<T>(
    String label,
    T initial,
    Map<T, String> choices,
    ValueChanged<T> update,
  ) => DropdownButtonFormField<T>(
    initialValue: initial,
    decoration: InputDecoration(
      labelText: label,
      border: const OutlineInputBorder(),
    ),
    isExpanded: true,
    items: [
      for (final entry in choices.entries)
        DropdownMenuItem(
          value: entry.key,
          child: Text(entry.value, overflow: TextOverflow.ellipsis),
        ),
    ],
    onChanged: (value) {
      if (value != null) setState(() => update(value));
    },
  );

  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppBar(
      title: Text(widget.existing == null ? 'New rule' : 'Edit rule'),
    ),
    body: Center(
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 600),
        child: Form(
          key: _form,
          child: ListView(
            padding: const EdgeInsets.fromLTRB(14, 8, 14, 26),
            children: [
              _field('name', 'Name', required: true),
              const SizedBox(height: 12),
              _picker('Trigger', _trigger, const {
                'offline': 'Device stops reporting',
                'value': 'Reading meets a condition',
                'event': 'Recorded event',
                'unusual': 'Unusual reading',
              }, (trigger) => _trigger = trigger),
              const SizedBox(height: 12),
              if (_trigger == 'value') ...[
                _field(
                  'path',
                  'Reading path',
                  example: 'battery.pct',
                  required: true,
                ),
                const SizedBox(height: 12),
                _picker('Comparison', _operator, const {
                  '>': 'Greater than',
                  '>=': 'At least',
                  '<': 'Less than',
                  '<=': 'At most',
                  '==': 'Equal to',
                  '!=': 'Different from',
                }, (value) => _operator = value),
                const SizedBox(height: 12),
                _picker('Value type', _valueType, const {
                  'number': 'Number',
                  'text': 'Text',
                  'boolean': 'True or false',
                }, (value) => _valueType = value),
                const SizedBox(height: 12),
                _field(
                  'value',
                  'Comparison value',
                  required: _valueType != 'text',
                ),
                const SizedBox(height: 12),
              ],
              if (_trigger == 'event') ...[
                _field(
                  'event',
                  'Event type',
                  example: 'power.battery',
                  required: true,
                ),
                const SizedBox(height: 12),
              ],
              if (_trigger == 'offline' || _trigger == 'value') ...[
                _field(
                  'duration',
                  widget.existing == null
                      ? 'Hold for minutes'
                      : 'Hold for seconds',
                  initial: _trigger != 'offline'
                      ? '0'
                      : widget.existing == null
                      ? '10'
                      : '600',
                  max: 7 * 24 * 60 * (widget.existing == null ? 1 : 60),
                ),
                const SizedBox(height: 12),
              ],
              _picker(
                'Applies to',
                _scope ?? (widget.existing == null ? ('', '') : ('saved', '')),
                {
                  if (widget.existing != null)
                    ('saved', ''): 'Keep saved scope',
                  ('', ''): 'Everything',
                  for (final entry in widget.devices.entries)
                    ('devices', entry.key): entry.value,
                  for (final entry in widget.things.entries)
                    ('things', entry.key): entry.value,
                },
                (choice) => _scope = choice.$1 == 'saved' ? null : choice,
              ),
              SwitchListTile(
                contentPadding: EdgeInsets.zero,
                title: const Text('Send a notification'),
                value: _notify,
                onChanged: (value) => setState(() => _notify = value),
              ),
              if (_notify) ...[
                _picker(
                  'Show notification on',
                  _notificationDestinationChanged || widget.existing == null
                      ? ('device', _notifyDevice ?? '')
                      : ('saved', ''),
                  {
                    if (widget.existing != null)
                      ('saved', ''): 'Keep saved destinations',
                    ('device', ''): 'All devices',
                    for (final entry in widget.devices.entries)
                      ('device', entry.key): entry.value,
                  },
                  (choice) {
                    _notificationDestinationChanged = choice.$1 != 'saved';
                    _notifyDevice = choice.$2.isEmpty ? null : choice.$2;
                  },
                ),
                const SizedBox(height: 6),
              ],
              CheckboxListTile(
                contentPadding: EdgeInsets.zero,
                title: const Text('Add a history note'),
                value: _annotate,
                onChanged: (value) =>
                    setState(() => _annotate = value ?? false),
              ),
              const SizedBox(height: 12),
              _field(
                'cooldown',
                widget.existing == null
                    ? 'Cooldown in minutes'
                    : 'Cooldown in seconds',
                initial: widget.existing == null ? '60' : '3600',
                max: 30 * 24 * 60 * (widget.existing == null ? 1 : 60),
              ),
              for (final problem in _problems)
                Padding(
                  padding: const EdgeInsets.only(top: 8),
                  child: Text(problem, style: const TextStyle(color: C.warn)),
                ),
              const SizedBox(height: 16),
              Align(
                alignment: Alignment.centerLeft,
                child: FilledButton(
                  onPressed: _saving ? null : _save,
                  child: const Text('Save rule'),
                ),
              ),
            ],
          ),
        ),
      ),
    ),
  );
}
