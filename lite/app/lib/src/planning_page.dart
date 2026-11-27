import 'package:material_ui/material_ui.dart';

import 'planning_draft.dart';
import 'planning_forms.dart';
import 'theme.dart';

const _design = Color(0xFFBCA6FF);

class PlanningPage extends StatefulWidget {
  const PlanningPage(
    this.drafts, {
    super.key,
    this.monitoredEquipment,
    this.openMonitorReport,
  });

  final List<ProjectDraft> drafts;
  final Map<String, String> Function()? monitoredEquipment;
  final void Function(BuildContext, String)? openMonitorReport;

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
    await Navigator.of(context).push<void>(
      MaterialPageRoute(
        builder: (_) => _ProjectPage(
          draft,
          monitoredEquipment: widget.monitoredEquipment,
          openMonitorReport: widget.openMonitorReport,
        ),
      ),
    );
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
              '${draft.rackUnits == null ? '' : '${draft.rackUnits}U rack · '}${draft.equipment.length} equipment entries · '
              '${draft.tasks.where((t) => !t.done).length} open tasks',
            ),
            trailing: const Icon(Icons.chevron_right),
          ),
        ),
    ]),
  );
}

class _ProjectPage extends StatefulWidget {
  const _ProjectPage(
    this.draft, {
    this.monitoredEquipment,
    this.openMonitorReport,
  });

  final ProjectDraft draft;
  final Map<String, String> Function()? monitoredEquipment;
  final void Function(BuildContext, String)? openMonitorReport;

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

  Future<void> _editEquipment({
    EquipmentDraft? item,
    int? unit,
    (String, String)? monitored,
  }) async {
    final edited = await editEquipment(
      context,
      draft,
      draft: item,
      unit: unit,
      monitored: monitored,
    );
    if (edited == null || !mounted) return;
    setState(() {
      if (item == null) {
        draft.equipment.add(edited);
      } else {
        draft.equipment[draft.equipment.indexOf(item)] = edited;
      }
    });
  }

  Future<void> _addMonitoredEquipment() async {
    final reports = widget.monitoredEquipment!().entries.toList()
      ..sort((a, b) => a.value.toLowerCase().compareTo(b.value.toLowerCase()));
    final chosen = await showDialog<MapEntry<String, String>>(
      context: context,
      builder: (context) => SimpleDialog(
        title: const Text('Choose monitored equipment'),
        children: [
          if (reports.isEmpty)
            const Padding(
              padding: EdgeInsets.symmetric(horizontal: 24, vertical: 10),
              child: Text('No reports are available on this device.'),
            ),
          for (final device in reports)
            SimpleDialogOption(
              onPressed: () => Navigator.pop(context, device),
              child: Text(device.value),
            ),
          SimpleDialogOption(
            onPressed: () => Navigator.pop(context),
            child: const Text('Cancel'),
          ),
        ],
      ),
    );
    if (chosen != null && mounted) {
      await _editEquipment(monitored: (chosen.key, chosen.value));
    }
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
    if (draft.rackUnits == null)
      Align(
        alignment: Alignment.centerLeft,
        child: FilledButton.tonalIcon(
          onPressed: _editProject,
          icon: const Icon(Icons.add),
          label: const Text('Add a rack'),
        ),
      )
    else ...[
      Text(
        '${draft.occupiedUnits} of ${draft.rackUnits}U occupied · '
        '${draft.rackDepthMm == null ? 'Depth unknown' : '${_number(draft.rackDepthMm!)} mm entered depth'}',
        style: const TextStyle(color: C.muted),
      ),
      _RackElevation(
        draft.rackUnits!,
        draft.equipment,
        onOpen: (item) => _editEquipment(item: item),
        onEmpty: (unit) => _editEquipment(unit: unit),
      ),
    ],
  ], page: 'rack');

  Widget _equipment() => _workspaceList([
    Wrap(
      spacing: 8,
      runSpacing: 8,
      children: [
        FilledButton.tonalIcon(
          onPressed: _editEquipment,
          icon: const Icon(Icons.add),
          label: const Text('Add equipment'),
        ),
        if (widget.monitoredEquipment != null)
          OutlinedButton.icon(
            onPressed: _addMonitoredEquipment,
            icon: const Icon(Icons.link),
            label: const Text('Add from Monitor'),
          ),
      ],
    ),
    if (draft.equipment.isNotEmpty) ...[
      Text(_intakeTotal('Weight', 'kg', (item) => item.weightKg)),
      Text(_intakeTotal('Power', 'W', (item) => item.powerW)),
    ],
    for (final item in draft.equipment)
      Card(
        child: Column(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            ListTile(
              onTap: () => _editEquipment(item: item),
              title: Text(item.name),
              subtitle: Text(_equipmentDetails(item)),
              trailing: IconButton(
                onPressed: () => _removeEquipment(item),
                tooltip: 'Remove ${item.name}',
                icon: const Icon(Icons.delete_outline),
              ),
            ),
            if (item.monitorId case final String id)
              if (widget.openMonitorReport case final open?)
                Padding(
                  padding: const EdgeInsets.only(left: 12, bottom: 6),
                  child: TextButton.icon(
                    onPressed: () => open(context, id),
                    icon: const Icon(Icons.open_in_new, size: 16),
                    label: const Text('Open monitor report'),
                  ),
                ),
          ],
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
      '${item.heightUnits == null ? 'Height unknown' : '${item.heightUnits}U'} · ${item.placement}',
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
    this.units,
    this.equipment, {
    required this.onOpen,
    required this.onEmpty,
  });

  final int units;
  final List<EquipmentDraft> equipment;
  final ValueChanged<EquipmentDraft> onOpen;
  final ValueChanged<int> onEmpty;
  static const _unitHeight = 44.0;

  @override
  Widget build(BuildContext context) {
    final byTopUnit = {
      for (final item in equipment)
        if (item.topUnit != null) item.topUnit!: item,
    };
    final rows = <Widget>[];
    for (var unit = units; unit >= 1;) {
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
