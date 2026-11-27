import 'package:material_ui/material_ui.dart';

import 'planning_draft.dart';

Future<ProjectDraft?> editProject(BuildContext context, [ProjectDraft? draft]) {
  final form = GlobalKey<FormState>();
  var name = draft?.name ?? '';
  var goal = draft?.goal ?? '';
  var units = draft?.rackUnits?.toString() ?? '';
  var depth = draft?.rackDepthMm?.toString() ?? '';
  return showDialog<ProjectDraft>(
    context: context,
    builder: (context) => AlertDialog(
      title: Text(draft == null ? 'New project' : 'Edit project'),
      scrollable: true,
      content: Form(
        key: form,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            _field(
              'Project name',
              name,
              (value) => name = value,
              validator: (value) => _required(value, 'Enter a project name.'),
            ),
            _field('Goal', goal, (value) => goal = value, lines: 2),
            _field(
              'Rack height (U), optional',
              units,
              (value) => units = value,
              numeric: true,
              validator: (value) {
                final text = value?.trim() ?? '';
                final used = draft?.highestOccupiedUnit ?? 0;
                if (text.isEmpty) {
                  if (used > 0) return 'Unplace the rack equipment first.';
                  return depth.trim().isEmpty
                      ? null
                      : 'Enter the height for this rack.';
                }
                final height = int.tryParse(text);
                if (height == null || height < 1 || height > 60) {
                  return 'Enter a whole number from 1 to 60.';
                }
                return height < used ? 'Equipment reaches U$used.' : null;
              },
            ),
            _field(
              'Rack depth (mm), optional',
              depth,
              (value) => depth = value,
              numeric: true,
              validator: _optionalPositive,
            ),
          ],
        ),
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.pop(context),
          child: const Text('Cancel'),
        ),
        FilledButton(
          onPressed: () {
            if (!form.currentState!.validate()) return;
            Navigator.pop(
              context,
              ProjectDraft(
                name.trim(),
                rackUnits: int.tryParse(units.trim()),
                rackDepthMm: double.tryParse(depth.trim()),
                goal: goal.trim(),
              ),
            );
          },
          child: Text(draft == null ? 'Create project' : 'Apply changes'),
        ),
      ],
    ),
  );
}

Future<EquipmentDraft?> editEquipment(
  BuildContext context,
  ProjectDraft project, {
  EquipmentDraft? draft,
  int? unit,
  (String id, String name)? monitored,
}) {
  final form = GlobalKey<FormState>();
  var name = draft?.name ?? monitored?.$2 ?? '';
  var height = draft?.heightUnits?.toString() ?? (unit == null ? '' : '1');
  var position = (draft?.bottomUnit ?? unit)?.toString() ?? '';
  var depth = draft?.depthMm?.toString() ?? '';
  var weight = draft?.weightKg?.toString() ?? '';
  var power = draft?.powerW?.toString() ?? '';

  EquipmentDraft equipment(int? heightUnits, int? bottomUnit) => EquipmentDraft(
    name: name.trim(),
    heightUnits: heightUnits,
    bottomUnit: bottomUnit,
    depthMm: double.tryParse(depth.trim()),
    weightKg: double.tryParse(weight.trim()),
    powerW: double.tryParse(power.trim()),
    monitorId: draft?.monitorId ?? monitored?.$1,
    source: draft?.source,
    id: draft?.id,
  );

  return showDialog<EquipmentDraft>(
    context: context,
    builder: (context) => AlertDialog(
      title: Text(draft == null ? 'Add equipment' : 'Edit equipment'),
      scrollable: true,
      content: Form(
        key: form,
        child: Column(
          mainAxisSize: MainAxisSize.min,
          children: [
            _field(
              'Equipment name',
              name,
              (value) => name = value,
              validator: (value) =>
                  _required(value, 'Enter an equipment name.'),
            ),
            _field(
              'Height (U), optional',
              height,
              (value) => height = value,
              numeric: true,
              validator: (value) {
                final text = value?.trim() ?? '';
                if (text.isEmpty) return null;
                final size = int.tryParse(text);
                return size == null || size < 1 || size > 60
                    ? 'Enter a whole number from 1 to 60.'
                    : null;
              },
            ),
            _field(
              'Bottom unit, optional',
              position,
              (value) => position = value,
              numeric: true,
              validator: (value) {
                final text = value?.trim() ?? '';
                if (text.isEmpty) return null;
                final bottom = int.tryParse(text);
                if (bottom == null) return 'Enter a whole unit number.';
                final size = int.tryParse(height.trim());
                if (height.trim().isNotEmpty &&
                    (size == null || size < 1 || size > 60)) {
                  return null;
                }
                return project.placementError(
                  equipment(size, bottom),
                  replacing: draft,
                );
              },
            ),
            _field(
              'Depth (mm), optional',
              depth,
              (value) => depth = value,
              numeric: true,
              validator: _optionalPositive,
            ),
            _field(
              'Weight (kg), optional',
              weight,
              (value) => weight = value,
              numeric: true,
              validator: _optionalPositive,
            ),
            _field(
              'Power (W), optional',
              power,
              (value) => power = value,
              numeric: true,
              validator: _optionalNonnegative,
            ),
          ],
        ),
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.pop(context),
          child: const Text('Cancel'),
        ),
        FilledButton(
          onPressed: () {
            if (!form.currentState!.validate()) return;
            Navigator.pop(
              context,
              equipment(
                int.tryParse(height.trim()),
                int.tryParse(position.trim()),
              ),
            );
          },
          child: Text(draft == null ? 'Add to project' : 'Apply changes'),
        ),
      ],
    ),
  );
}

Future<String?> addProjectTask(BuildContext context) {
  final form = GlobalKey<FormState>();
  var title = '';
  return showDialog<String>(
    context: context,
    builder: (context) => AlertDialog(
      title: const Text('New task'),
      scrollable: true,
      content: Form(
        key: form,
        child: _field(
          'Task',
          title,
          (value) => title = value,
          validator: (value) => _required(value, 'Enter a task.'),
        ),
      ),
      actions: [
        TextButton(
          onPressed: () => Navigator.pop(context),
          child: const Text('Cancel'),
        ),
        FilledButton(
          onPressed: () {
            if (form.currentState!.validate()) {
              Navigator.pop(context, title.trim());
            }
          },
          child: const Text('Add task'),
        ),
      ],
    ),
  );
}

Widget _field(
  String label,
  String initialValue,
  ValueChanged<String> onChanged, {
  FormFieldValidator<String>? validator,
  bool numeric = false,
  int lines = 1,
}) => Padding(
  padding: const EdgeInsets.only(bottom: 12),
  child: TextFormField(
    key: ValueKey(label),
    initialValue: initialValue,
    onChanged: onChanged,
    validator: validator,
    maxLines: lines,
    maxLength: numeric ? 20 : (lines > 1 ? 400 : 100),
    keyboardType: numeric
        ? const TextInputType.numberWithOptions(decimal: true)
        : null,
    decoration: InputDecoration(
      labelText: label,
      border: const OutlineInputBorder(),
      counterText: '',
      errorMaxLines: 2,
    ),
  ),
);

String? _required(String? value, String message) =>
    value?.trim().isNotEmpty == true ? null : message;

String? _optionalPositive(String? value) =>
    _optionalNumber(value, allowZero: false);
String? _optionalNonnegative(String? value) =>
    _optionalNumber(value, allowZero: true);

String? _optionalNumber(String? value, {required bool allowZero}) {
  final text = value?.trim() ?? '';
  if (text.isEmpty) return null;
  final number = double.tryParse(text);
  if (number == null ||
      !number.isFinite ||
      (allowZero ? number < 0 : number <= 0)) {
    return allowZero
        ? 'Enter zero or a positive number.'
        : 'Enter a positive number.';
  }
  return null;
}
