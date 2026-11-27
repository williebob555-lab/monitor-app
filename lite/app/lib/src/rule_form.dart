import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';

import 'theme.dart';

class RuleForm extends StatefulWidget {
  const RuleForm(this.root, this.devices, this.things, {super.key});

  final String root;
  final Map<String, String> devices;
  final Map<String, String> things;

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

  void _save() {
    if (!_form.currentState!.validate()) return;
    final duration =
        int.tryParse(_text['duration'] ?? '') ??
        (_trigger == 'offline' ? 10 : 0);
    final cooldown = int.tryParse(_text['cooldown'] ?? '') ?? 60;
    final value = _text['value'] ?? '';
    final number = num.tryParse(value);
    final rule = Rule(
      id: newModelId(),
      name: _text['name']!,
      enabled: false,
      when: {
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
        if (_trigger == 'offline' || _trigger == 'value') 'for': duration * 60,
      },
      scope: {
        if (_scope case (final group, final id)) group: [id],
      },
      then: [
        if (_notify)
          {
            'type': 'notify',
            if (_notifyDevice != null) 'on': [_notifyDevice!],
          },
        if (_annotate) {'type': 'annotate'},
      ],
      cooldown: cooldown * 60,
    );
    final problems = validateRule(rule.toJson());
    if (problems.isNotEmpty) {
      setState(() => _problems = problems);
      return;
    }
    try {
      RuleSet.save(widget.root, rule);
      Navigator.pop(context, true);
    } on Object {
      setState(
        () => _problems = ['Could not save the rule to the Monitor folder.'],
      );
    }
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
    appBar: AppBar(title: const Text('New rule')),
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
                  'Hold for minutes',
                  initial: _trigger == 'offline' ? '10' : '0',
                  max: 7 * 24 * 60,
                ),
                const SizedBox(height: 12),
              ],
              _picker('Applies to', _scope ?? ('', ''), {
                ('', ''): 'Everything',
                for (final entry in widget.devices.entries)
                  ('devices', entry.key): entry.value,
                for (final entry in widget.things.entries)
                  ('things', entry.key): entry.value,
              }, (choice) => _scope = choice.$1.isEmpty ? null : choice),
              SwitchListTile(
                contentPadding: EdgeInsets.zero,
                title: const Text('Send a notification'),
                value: _notify,
                onChanged: (value) => setState(() => _notify = value),
              ),
              if (_notify) ...[
                _picker('Show notification on', _notifyDevice ?? '', {
                  '': 'All devices',
                  ...widget.devices,
                }, (id) => _notifyDevice = id.isEmpty ? null : id),
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
                'Cooldown in minutes',
                initial: '60',
                max: 30 * 24 * 60,
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
                  onPressed: _save,
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
