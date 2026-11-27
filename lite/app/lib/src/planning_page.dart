import 'package:flutter/services.dart';
import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';

import 'planning_draft.dart';
import 'planning_forms.dart';
import 'project_needs_page.dart';
import 'theme.dart';

const _design = Color(0xFFBCA6FF);

class PlanningPage extends StatefulWidget {
  const PlanningPage(
    this.drafts, {
    super.key,
    this.store,
    this.monitoredEquipment,
    this.openMonitorReport,
  });

  final List<ProjectDraft> drafts;
  final ProjectStore? store;
  final Map<String, String> Function()? monitoredEquipment;
  final void Function(BuildContext, String)? openMonitorReport;

  @override
  State<PlanningPage> createState() => _PlanningPageState();
}

class _PlanningPageState extends State<PlanningPage> {
  String? _loadError;

  @override
  void initState() {
    super.initState();
    _loadProjects();
  }

  void _loadProjects() {
    final store = widget.store;
    if (store == null) return;
    try {
      final saved = store.list();
      final drafts = widget.drafts;
      drafts.removeWhere((draft) => !draft.dirty);
      final editing = drafts.map((draft) => draft.source?.id).toSet();
      drafts.addAll(
        saved
            .where((p) => !editing.contains(p.id))
            .map(ProjectDraft.fromProject),
      );
      _loadError = null;
    } on Exception {
      _loadError = 'Could not open saved projects. Check the Monitor folder, then try again.';
    }
  }

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
          store: widget.store,
          monitoredEquipment: widget.monitoredEquipment,
          openMonitorReport: widget.openMonitorReport,
        ),
      ),
    );
    if (mounted) setState(_loadProjects);
  }

  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppBar(
      title: const Text('Projects'),
      actions: [
        if (widget.store != null)
          IconButton(
            tooltip: 'Reload projects',
            onPressed: () => setState(_loadProjects),
            icon: const Icon(Icons.refresh),
          ),
      ],
    ),
    body: _workspaceList([
      if (widget.store == null)
        const Text(
          'Drafts stay in this session and clear when the app closes.',
          style: TextStyle(color: _design),
        ),
      if (_loadError case final error?)
        Text(error, style: const TextStyle(color: C.warn)),
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
              '${draft.tasks.where((t) => !t.done).length} open tasks'
              '${draft.dirty && widget.store != null ? ' · Unsaved' : ''}',
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
    this.store,
    this.monitoredEquipment,
    this.openMonitorReport,
  });

  final ProjectDraft draft;
  final ProjectStore? store;
  final Map<String, String> Function()? monitoredEquipment;
  final void Function(BuildContext, String)? openMonitorReport;

  @override
  State<_ProjectPage> createState() => _ProjectPageState();
}

class _ProjectPageState extends State<_ProjectPage> {
  ProjectDraft get draft => widget.draft;
  String? _saveError;
  bool _rear = false;

  void _change(VoidCallback change) => setState(() {
    change();
    draft.dirty = true;
    _saveError = null;
  });

  void _save() {
    try {
      draft.save(widget.store!);
      setState(() => _saveError = null);
    } on StateError catch (error) {
      setState(() => _saveError = error.message);
    } on Exception {
      setState(
        () => _saveError = 'Could not save this project. Check the Monitor folder and try again.',
      );
    }
  }

  Future<void> _reopenSavedProject() async {
    final reload = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: const Text('Discard unsaved edits?'),
        content: const Text('You can then reopen the latest saved project.'),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () => Navigator.pop(context, true),
            child: const Text('Discard edits'),
          ),
        ],
      ),
    );
    if (reload != true || !mounted) return;
    draft.dirty = false;
    Navigator.of(context).pop();
  }

  Future<void> _editProject() async {
    final edited = await editProject(context, draft);
    if (edited == null || !mounted) return;
    _change(() {
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
    (String, String)? inventory,
  }) async {
    final edited = await editEquipment(
      context,
      draft,
      draft: item,
      unit: unit,
      monitored: monitored,
      inventory: inventory,
    );
    if (edited == null || !mounted) return;
    _change(() {
      if (item == null) {
        draft.equipment.add(edited);
      } else {
        draft.equipment[draft.equipment.indexOf(item)] = edited;
      }
    });
  }

  Future<void> _addInventoryEquipment() async {
    try {
      final store = widget.store!;
      final items = ModelStore(store.root, store.deviceId).list()
        ..sort((a, b) => a.name.toLowerCase().compareTo(b.name.toLowerCase()));
      final chosen = await showDialog<Item>(
        context: context,
        builder: (context) => SimpleDialog(
          title: const Text('Choose inventory equipment'),
          children: [
            if (items.isEmpty)
              const Padding(
                padding: EdgeInsets.symmetric(horizontal: 24, vertical: 10),
                child: Text('No inventory items have been added yet.'),
              ),
            for (final item in items)
              SimpleDialogOption(
                onPressed: () => Navigator.pop(context, item),
                child: Text(item.name),
              ),
            SimpleDialogOption(
              onPressed: () => Navigator.pop(context),
              child: const Text('Cancel'),
            ),
          ],
        ),
      );
      if (chosen != null && mounted) {
        await _editEquipment(inventory: (chosen.id, chosen.name));
      }
    } on Exception {
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          const SnackBar(content: Text('Could not open inventory.')),
        );
      }
    }
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
    _change(() => draft.tasks.add(ProjectTaskDraft(title)));
  }

  Future<void> _copyPlan() async {
    final afterRack = (draft.rackUnits ?? 0) + 1;
    final equipment = [...draft.equipment]
      ..sort(
        (a, b) =>
            (a.bottomUnit ?? afterRack).compareTo(b.bottomUnit ?? afterRack),
      );
    final lines = [
      draft.name,
      'Project draft',
      if (draft.goal.isNotEmpty) draft.goal,
      if (draft.rackUnits case final int units)
        'Rack · ${units}U · ${draft.rackDepthMm == null ? 'depth unknown' : '${_number(draft.rackDepthMm!)} mm entered depth'}',
      if (equipment.isNotEmpty) ...[
        '',
        'Equipment',
        for (final item in equipment)
          '- ${item.placement} · ${item.name} · ${item.heightUnits == null ? 'height unknown' : '${item.heightUnits}U'}'
              '${item.depthMm == null ? '' : ' · ${_number(item.depthMm!)} mm entered depth'}'
              '${item.weightKg == null ? '' : ' · ${_number(item.weightKg!)} kg entered weight'}'
              '${item.powerW == null ? '' : ' · ${_number(item.powerW!)} W entered power'}',
      ],
      if (draft.tasks.isNotEmpty) ...[
        '',
        'Tasks',
        for (final task in draft.tasks)
          '- [${task.done ? 'x' : ' '}] ${task.title}',
      ],
    ];
    var message = 'Project draft copied';
    try {
      await Clipboard.setData(ClipboardData(text: lines.join('\n')));
    } on PlatformException {
      message = 'Could not copy the project draft';
    }
    if (mounted) {
      ScaffoldMessenger.of(context)
          .showSnackBar(SnackBar(content: Text(message)));
    }
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
      _change(() => draft.equipment.remove(item));
    }
  }

  @override
  Widget build(BuildContext context) => DefaultTabController(
    length: 3,
    child: Scaffold(
      appBar: AppBar(
        title: Text(draft.name),
        actions: [
          if (widget.store != null)
            IconButton(
              tooltip: 'Save project',
              onPressed: draft.dirty ? _save : null,
              icon: const Icon(Icons.save_outlined),
            ),
          IconButton(
            tooltip: 'Copy plan',
            onPressed: _copyPlan,
            icon: const Icon(Icons.copy_outlined),
          ),
          IconButton(
            tooltip: 'Edit project',
            onPressed: _editProject,
            icon: const Icon(Icons.edit_outlined),
          ),
        ],
        bottom: const TabBar(
          dividerColor: C.line,
          tabs: [
            Tab(text: 'Rack'),
            Tab(text: 'Equipment'),
            Tab(text: 'Tasks'),
          ],
        ),
      ),
      body: Column(
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(16, 10, 16, 0),
            child: Align(
              alignment: Alignment.centerLeft,
              child: Text(
                _saveError ??
                    (widget.store == null
                        ? 'Session draft'
                        : draft.dirty
                        ? 'Unsaved changes'
                        : 'Saved to Monitor folder'),
                style: TextStyle(color: _saveError == null ? C.muted : C.warn),
              ),
            ),
          ),
          if (_saveError != null && draft.source != null)
            TextButton(
              onPressed: _reopenSavedProject,
              child: const Text('Back to saved projects'),
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
    Align(
      alignment: Alignment.centerLeft,
      child: TextButton.icon(
        onPressed: () => Navigator.of(context).push<void>(
          MaterialPageRoute(
            builder: (_) => ProjectNeedsPage(draft.toProject(), widget.store),
          ),
        ),
        icon: const Icon(Icons.checklist_outlined),
        label: const Text('Review what’s needed'),
      ),
    ),
    if (draft.rackUnits == null)
      Align(
        alignment: Alignment.centerLeft,
        child: FilledButton.tonalIcon(
          onPressed: _editProject,
          icon: const Icon(Icons.add),
          label: const Text('Add a rack'),
        ),
      )
    else if (draft.rackUnits! < 1 || draft.rackUnits! > 60)
      const Text('The rack view supports heights from 1 to 60U.')
    else ...[
      if (draft.equipment.any((e) => e.face == 'rear'))
        Align(
          alignment: Alignment.centerLeft,
          child: SegmentedButton<bool>(
            segments: const [
              ButtonSegment(value: false, label: Text('Front')),
              ButtonSegment(value: true, label: Text('Rear')),
            ],
            selected: {_rear},
            onSelectionChanged: (side) => setState(() => _rear = side.single),
          ),
        ),
      Text(
        '${draft.occupiedUnits} of ${draft.rackUnits}U occupied · '
        '${draft.rackDepthMm == null ? 'Depth unknown' : '${_number(draft.rackDepthMm!)} mm entered depth'}',
        style: const TextStyle(color: C.muted),
      ),
      _RackElevation(
        draft.rackUnits!,
        draft.equipment
            .where((e) => e.fullDepth || (e.face == 'rear') == _rear)
            .toList(),
        depthMm: draft.rackDepthMm,
        onOpen: (item) => _editEquipment(item: item),
        onEmpty: _rear ? null : (unit) => _editEquipment(unit: unit),
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
        if (widget.store != null)
          OutlinedButton.icon(
            onPressed: _addInventoryEquipment,
            icon: const Icon(Icons.inventory_2_outlined),
            label: const Text('Add from inventory'),
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
          onChanged: (done) => _change(() => task.done = done ?? false),
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
      if (item.inventoryId != null) 'Linked to inventory',
      if (item.bottomUnit != null &&
          item.topUnit != null &&
          item.heightUnits != item.topUnit! - item.bottomUnit! + 1)
        'Equipment height differs from its rack position',
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
    required this.depthMm,
    required this.onOpen,
    required this.onEmpty,
  });

  final int units;
  final List<EquipmentDraft> equipment;
  final double? depthMm;
  final ValueChanged<EquipmentDraft> onOpen;
  final ValueChanged<int>? onEmpty;
  static const _unitHeight = 44.0;
  static const _railWidth = 22.0;
  static const _rulerWidth = 42.0;
  static const _unitMark = Color(0xFF7F909F);
  static const _frame = Color(0xFF0E1114);

  @override
  Widget build(BuildContext context) {
    final byTopUnit = {
      for (final item in equipment)
        if (item.bottomUnit != null &&
            item.bottomUnit! >= 1 &&
            item.topUnit != null &&
            item.topUnit! >= item.bottomUnit! &&
            item.topUnit! <= units)
          item.topUnit!: item,
    };
    final bays = <Widget>[];
    for (var unit = units; unit >= 1;) {
      final item = byTopUnit[unit];
      final count = item == null ? 1 : unit - item.bottomUnit! + 1;
      final top = unit;
      bays.add(
        SizedBox(
          height: _unitHeight * count,
          child: item == null ? _emptyBay(top) : _faceplate(item),
        ),
      );
      unit -= count;
    }

    return Align(
      alignment: Alignment.topLeft,
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 560),
        child: Row(
          crossAxisAlignment: CrossAxisAlignment.start,
          children: [
            Padding(
              padding: const EdgeInsets.only(top: 9),
              child: SizedBox(
                width: _rulerWidth,
                child: Column(
                  children: [
                    for (var unit = units; unit > 0; unit--)
                      Container(
                        height: _unitHeight,
                        alignment: Alignment.centerRight,
                        padding: const EdgeInsets.only(right: 7),
                        decoration: BoxDecoration(
                          border: Border(
                            top: unit == units
                                ? const BorderSide(color: _unitMark)
                                : BorderSide.none,
                            bottom: const BorderSide(color: _unitMark),
                          ),
                        ),
                        child: Text(
                          'U$unit',
                          semanticsLabel: 'Rack unit $unit',
                          style: const TextStyle(
                            color: Color(0xFFD1D9E1),
                            fontSize: 11,
                            fontWeight: FontWeight.w600,
                            fontFeatures: [FontFeature.tabularFigures()],
                          ),
                        ),
                      ),
                  ],
                ),
              ),
            ),
            Expanded(
              child: Container(
                padding: const EdgeInsets.all(8),
                decoration: BoxDecoration(
                  color: _frame,
                  border: Border.all(color: C.line),
                  borderRadius: BorderRadius.circular(5),
                ),
                child: Row(
                  crossAxisAlignment: CrossAxisAlignment.start,
                  children: [
                    _rail(),
                    Expanded(
                      child: Column(
                        crossAxisAlignment: CrossAxisAlignment.stretch,
                        children: bays,
                      ),
                    ),
                    _rail(),
                  ],
                ),
              ),
            ),
          ],
        ),
      ),
    );
  }

  Widget _rail() => SizedBox(
    width: _railWidth,
    height: _unitHeight * units,
    child: CustomPaint(painter: _RackRail(units, _unitHeight, _unitMark)),
  );

  Widget _emptyBay(int unit) => Material(
    color: const Color(0xFF090C0F),
    shape: const Border(bottom: BorderSide(color: Color(0xFF252D35))),
    child: Tooltip(
      message: onEmpty == null ? 'Empty U$unit' : 'Add at U$unit',
      excludeFromSemantics: true,
      child: Semantics(
        button: onEmpty != null,
        label: onEmpty == null ? 'Empty U$unit' : 'Add at U$unit',
        child: InkWell(
          key: ValueKey('rack-empty:$unit'),
          onTap: onEmpty == null ? null : () => onEmpty!(unit),
          child: onEmpty == null
              ? null
              : const Center(child: Icon(Icons.add, size: 16, color: C.muted)),
        ),
      ),
    ),
  );

  Widget _faceplate(EquipmentDraft item) {
    final tooDeep =
        depthMm != null && item.depthMm != null && item.depthMm! > depthMm!;
    return Padding(
      padding: const EdgeInsets.only(bottom: 1),
      child: Material(
        color: C.raised,
        shape: const Border(
          top: BorderSide(color: Color(0xFF37404A)),
          bottom: BorderSide(color: Colors.black),
        ),
        child: InkWell(
          onTap: () => onOpen(item),
          child: Padding(
            padding: const EdgeInsets.symmetric(horizontal: 10),
            child: Row(
              children: [
                Expanded(
                  child: Text(
                    item.name,
                    maxLines: 1,
                    overflow: TextOverflow.ellipsis,
                    style: const TextStyle(
                      fontSize: 13,
                      fontWeight: FontWeight.w600,
                    ),
                  ),
                ),
                if (tooDeep)
                  const Padding(
                    padding: EdgeInsets.only(left: 8),
                    child: Tooltip(
                      message: 'Depth exceeds entered rack depth',
                      child: Icon(
                        Icons.warning_amber_rounded,
                        color: C.warn,
                        size: 16,
                      ),
                    ),
                  ),
                const SizedBox(width: 8),
                Text(
                  '${item.heightUnits}U',
                  style: const TextStyle(color: C.muted, fontSize: 11),
                ),
              ],
            ),
          ),
        ),
      ),
    );
  }
}

class _RackRail extends CustomPainter {
  const _RackRail(this.units, this.unitHeight, this.unitMark);

  final int units;
  final double unitHeight;
  final Color unitMark;

  @override
  void paint(Canvas canvas, Size size) {
    final steel = Paint()..color = const Color(0xFF2C343D);
    final alternate = Paint()..color = const Color(0xFF353E48);
    for (var unit = 0; unit < units; unit++) {
      canvas.drawRect(
        Rect.fromLTWH(0, unit * unitHeight, size.width, unitHeight),
        unit.isEven ? steel : alternate,
      );
    }
    final groove = Paint()..color = const Color(0xFF39414B);
    canvas.drawLine(const Offset(1, 0), Offset(1, size.height), groove);
    final hole = Paint()..color = const Color(0xFF101317);
    final x = size.width / 2;
    for (var unit = 0; unit < units; unit++) {
      for (final position in const [1 / 7, 1 / 2, 6 / 7]) {
        final y = (unit + position) * unitHeight;
        final slot = Rect.fromCenter(center: Offset(x, y), width: 5, height: 4);
        canvas.drawRRect(
          RRect.fromRectAndRadius(slot, const Radius.circular(0.7)),
          hole,
        );
      }
    }
    final etched = Paint()
      ..color = unitMark
      ..strokeWidth = 1.25;
    for (var unit = 0; unit <= units; unit++) {
      final y = (unit * unitHeight).clamp(0.75, size.height - 0.75).toDouble();
      canvas.drawLine(Offset(1, y), Offset(size.width - 1, y), etched);
    }
  }

  @override
  bool shouldRepaint(_RackRail oldDelegate) =>
      units != oldDelegate.units ||
      unitHeight != oldDelegate.unitHeight ||
      unitMark != oldDelegate.unitMark;
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
