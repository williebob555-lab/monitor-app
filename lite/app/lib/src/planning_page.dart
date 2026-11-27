import 'package:material_ui/material_ui.dart';

import 'planning_draft.dart';
import 'planning_forms.dart';
import 'theme.dart';

const _design = Color(0xFFBCA6FF);

class PlanningPage extends StatefulWidget {
  const PlanningPage(this.drafts, {super.key});

  final List<ProjectDraft> drafts;

  @override
  State<PlanningPage> createState() => _PlanningPageState();
}

class _PlanningPageState extends State<PlanningPage> {
  Future<void> _newProject() async {
    final draft = await editProject(context);
    if (draft == null || !mounted) return;
    setState(() => widget.drafts.add(draft));
    await _open(draft);
  }

  Future<void> _open(ProjectDraft draft) async {
    await Navigator.of(context)
        .push<void>(MaterialPageRoute(builder: (_) => _ProjectPage(draft)));
    if (mounted) setState(() {});
  }

  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppBar(title: const Text('Projects')),
    body: _workspaceList([
      const Text(
        'Drafts stay in this session and clear when the app closes.',
        style: TextStyle(color: _design),
      ),
      Align(
        alignment: Alignment.centerLeft,
        child: FilledButton.icon(
          onPressed: _newProject,
          icon: const Icon(Icons.add),
          label: const Text('New project'),
        ),
      ),
      for (final draft in widget.drafts)
        Card(
          child: ListTile(
            onTap: () => _open(draft),
            leading: const Icon(Icons.architecture, color: _design),
            title: Text(draft.name),
            subtitle: Text(
              '${draft.rackUnits}U rack · ${draft.equipment.length} equipment entries · '
              '${draft.tasks.where((t) => !t.done).length} open tasks',
            ),
            trailing: const Icon(Icons.chevron_right),
          ),
        ),
    ]),
  );
}

class _ProjectPage extends StatefulWidget {
  const _ProjectPage(this.draft);

  final ProjectDraft draft;

  @override
  State<_ProjectPage> createState() => _ProjectPageState();
}

class _ProjectPageState extends State<_ProjectPage> {
  ProjectDraft get draft => widget.draft;

  Future<void> _editProject() async {
    final edited = await editProject(context, draft);
    if (edited == null || !mounted) return;
    setState(() {
      draft.name = edited.name;
      draft.goal = edited.goal;
      draft.rackUnits = edited.rackUnits;
      draft.rackDepthMm = edited.rackDepthMm;
    });
  }

  Future<void> _editEquipment({EquipmentDraft? item, int? unit}) async {
    final edited = await editEquipment(context, draft, draft: item, unit: unit);
    if (edited == null || !mounted) return;
    setState(() {
      if (item == null) {
        draft.equipment.add(edited);
      } else {
        draft.equipment[draft.equipment.indexOf(item)] = edited;
      }
    });
  }

  Future<void> _addTask() async {
    final title = await addProjectTask(context);
    if (title == null || !mounted) return;
    setState(() => draft.tasks.add(ProjectTaskDraft(title)));
  }

  Future<void> _removeEquipment(EquipmentDraft item) async {
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: Text('Remove ${item.name}?'),
        content: const Text('This removes the equipment from the draft.'),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(context, true),
            child: const Text('Remove'),
          ),
        ],
      ),
    );
    if (confirmed == true && mounted) {
      setState(() => draft.equipment.remove(item));
    }
  }

  @override
  Widget build(BuildContext context) => DefaultTabController(
    length: 3,
    child: Scaffold(
      appBar: AppBar(
        title: Text(draft.name),
        actions: [
          IconButton(
            tooltip: 'Edit project',
            onPressed: _editProject,
            icon: const Icon(Icons.edit_outlined),
          ),
        ],
        bottom: const TabBar(
          tabs: [
            Tab(text: 'Rack'),
            Tab(text: 'Equipment'),
            Tab(text: 'Tasks'),
          ],
        ),
      ),
      body: Column(
        children: [
          const Padding(
            padding: EdgeInsets.fromLTRB(16, 10, 16, 0),
            child: Align(
              alignment: Alignment.centerLeft,
              child: Text('Session draft', style: TextStyle(color: _design)),
            ),
          ),
          Expanded(
            child: TabBarView(children: [_rack(), _equipment(), _tasks()]),
          ),
        ],
      ),
    ),
  );

  Widget _rack() => _workspaceList([
    if (draft.goal.isNotEmpty)
      Text(draft.goal, style: const TextStyle(fontSize: 17)),
    Text(
      '${draft.occupiedUnits} of ${draft.rackUnits}U occupied · '
      '${draft.rackDepthMm == null ? 'Depth unknown' : '${_number(draft.rackDepthMm!)} mm entered depth'}',
      style: const TextStyle(color: C.muted),
    ),
    _RackElevation(
      draft,
      onOpen: (item) => _editEquipment(item: item),
      onEmpty: (unit) => _editEquipment(unit: unit),
    ),
  ], page: 'rack');

  Widget _equipment() => _workspaceList([
    Align(
      alignment: Alignment.centerLeft,
      child: FilledButton.tonalIcon(
        onPressed: _editEquipment,
        icon: const Icon(Icons.add),
        label: const Text('Add equipment'),
      ),
    ),
    if (draft.equipment.isNotEmpty) ...[
      Text(_intakeTotal('Weight', 'kg', (item) => item.weightKg)),
      Text(_intakeTotal('Power', 'W', (item) => item.powerW)),
    ],
    for (final item in draft.equipment)
      Card(
        child: ListTile(
          onTap: () => _editEquipment(item: item),
          title: Text(item.name),
          subtitle: Text(_equipmentDetails(item)),
          trailing: IconButton(
            onPressed: () => _removeEquipment(item),
            tooltip: 'Remove ${item.name}',
            icon: const Icon(Icons.delete_outline),
          ),
        ),
      ),
  ], page: 'equipment');

  Widget _tasks() => _workspaceList([
    Align(
      alignment: Alignment.centerLeft,
      child: FilledButton.tonalIcon(
        onPressed: _addTask,
        icon: const Icon(Icons.add),
        label: const Text('New task'),
      ),
    ),
    for (final task in draft.tasks)
      Card(
        child: CheckboxListTile(
          title: Text(task.title),
          value: task.done,
          onChanged: (done) => setState(() => task.done = done ?? false),
        ),
      ),
  ], page: 'tasks');

  String _intakeTotal(
    String name,
    String unit,
    double? Function(EquipmentDraft) read,
  ) {
    final known = draft.equipment.map(read).whereType<double>().toList();
    if (known.isEmpty) return '$name unknown';
    final total = known.fold(0.0, (sum, value) => sum + value);
    final coverage = '${known.length} of ${draft.equipment.length} entries';
    return '$name entered for $coverage · ${_number(total)} $unit';
  }

  String _equipmentDetails(EquipmentDraft item) {
    final depth = item.depthMm;
    final rackDepth = draft.rackDepthMm;
    return [
      '${item.heightUnits}U · ${item.placement}',
      if (depth != null && rackDepth != null && depth > rackDepth)
        'Depth exceeds entered rack depth'
      else if (depth == null || rackDepth == null)
        'Depth check unavailable'
      else
        '${_number(depth)} mm entered depth',
    ].join('\n');
  }
}

class _RackElevation extends StatelessWidget {
  const _RackElevation(
    this.draft, {
    required this.onOpen,
    required this.onEmpty,
  });

  final ProjectDraft draft;
  final ValueChanged<EquipmentDraft> onOpen;
  final ValueChanged<int> onEmpty;
  static const _unitHeight = 44.0;

  @override
  Widget build(BuildContext context) {
    final byTopUnit = {
      for (final item in draft.equipment)
        if (item.topUnit != null) item.topUnit!: item,
    };
    final rows = <Widget>[];
    for (var unit = draft.rackUnits; unit >= 1;) {
      final item = byTopUnit[unit];
      final count = item?.heightUnits ?? 1;
      final top = unit;
      rows.add(
        Row(
          children: [
            SizedBox(
              width: 52,
              child: Column(
                children: [
                  for (var tick = top; tick > top - count; tick--)
                    SizedBox(
                      height: _unitHeight,
                      child: Center(
                        child: Text(
                          'U$tick',
                          style: const TextStyle(color: C.muted),
                        ),
                      ),
                    ),
                ],
              ),
            ),
            Expanded(
              child: SizedBox(
                height: _unitHeight * count,
                child: Material(
                  color: item == null
                      ? C.surface
                      : _design.withValues(alpha: 0.13),
                  shape: Border(
                    bottom: BorderSide(color: item == null ? C.line : _design),
                  ),
                  child: InkWell(
                    onTap: item == null
                        ? () => onEmpty(top)
                        : () => onOpen(item),
                    child: Padding(
                      padding: const EdgeInsets.symmetric(horizontal: 12),
                      child: Row(
                        children: [
                          if (item == null)
                            const Icon(Icons.add, size: 17, color: C.muted),
                          if (item == null) const SizedBox(width: 7),
                          Expanded(
                            child: Text(
                              item == null ? 'Add at U$top' : item.name,
                              maxLines: 1,
                              overflow: TextOverflow.ellipsis,
                              style: TextStyle(
                                color: item == null ? C.muted : C.text,
                              ),
                            ),
                          ),
                          if (item != null)
                            const Icon(
                              Icons.chevron_right,
                              size: 18,
                              color: _design,
                            ),
                        ],
                      ),
                    ),
                  ),
                ),
              ),
            ),
          ],
        ),
      );
      unit -= count;
    }
    return DecoratedBox(
      decoration: BoxDecoration(
        color: C.raised,
        border: Border.all(color: C.line),
      ),
      child: Column(children: rows),
    );
  }
}

Widget _workspaceList(List<Widget> children, {String page = 'projects'}) =>
    Center(
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 760),
        child: ListView.separated(
          key: PageStorageKey(page),
          padding: const EdgeInsets.fromLTRB(16, 14, 16, 28),
          itemBuilder: (_, index) => children[index],
          separatorBuilder: (_, _) => const SizedBox(height: 10),
          itemCount: children.length,
        ),
      ),
    );

String _number(double value) {
  final text = value.toString();
  return text.endsWith('.0') ? text.substring(0, text.length - 2) : text;
}
