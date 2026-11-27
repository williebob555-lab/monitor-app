import 'package:monitor_core/monitor_core.dart';

/// Working copy of one project and its first rack.
class ProjectDraft {
  ProjectDraft(this.name, {this.rackUnits, this.rackDepthMm, this.goal = ''});

  factory ProjectDraft.fromProject(Project project) {
    final rack = project.assemblies.where((a) => a.type == 'rack').firstOrNull;
    return ProjectDraft(
        project.name,
        goal: project.goal,
        rackUnits: rack?.units,
        rackDepthMm: rack?.depth?.base('length'),
      )
      ..source = project
      .._rack = rack
      ..dirty = false
      ..equipment.addAll(
        rack?.placements.map(EquipmentDraft.fromPlacement) ?? [],
      )
      ..tasks.addAll(
        project.tasks.map(
          (t) => ProjectTaskDraft(t.text, id: t.id, done: t.done),
        ),
      );
  }

  String name;
  String goal;
  int? rackUnits;
  double? rackDepthMm;
  Project? source;
  Assembly? _rack;
  bool dirty = true;
  final equipment = <EquipmentDraft>[];
  final tasks = <ProjectTaskDraft>[];

  void save(ProjectStore store) {
    final previous = source;
    if (previous != null) {
      final latest = store.get(previous.id);
      if (latest == null ||
          latest.rev != previous.rev ||
          latest.by != previous.by ||
          latest.time?.millisecondsSinceEpoch !=
              previous.time?.millisecondsSinceEpoch) {
        throw StateError(
          'This project changed on disk. Your edits are still open.',
        );
      }
    }

    final saved = store.put(toProject());
    source = saved;
    _rack = saved.assemblies.where((a) => a.type == 'rack').firstOrNull;
    if (_rack case final rack?) {
      for (var i = 0; i < equipment.length; i++) {
        equipment[i] = EquipmentDraft.fromPlacement(rack.placements[i]);
      }
    }
    dirty = false;
  }

  Project toProject() {
    final previous = source;
    final rack = _rack;
    final updatedRack =
        rack != null || rackUnits != null || equipment.isNotEmpty
        ? Assembly(
            id: rack?.id ?? newModelId(),
            name: rack?.name ?? 'Rack',
            units: rackUnits,
            depth: _quantity(rack?.depth, rackDepthMm, 'length', 'mm'),
            maxWeight: rack?.maxWeight,
            maxPower: rack?.maxPower,
            extra: rack?.extra,
            placements: equipment.map((e) => e.toPlacement()).toList(),
          )
        : null;
    return Project(
      id: previous?.id ?? newModelId(),
      name: name,
      goal: goal,
      state: previous?.state ?? 'draft',
      items: previous?.items,
      extra: previous?.extra,
      tasks: [for (final t in tasks) Task(t.id, t.title, done: t.done)],
      assemblies: [
        for (final other in previous?.assemblies ?? <Assembly>[])
          if (identical(other, rack)) updatedRack! else other,
        if (rack == null && updatedRack != null) updatedRack,
      ],
    );
  }

  String? placementError(EquipmentDraft item, {EquipmentDraft? replacing}) {
    final bottom = item.bottomUnit;
    if (bottom == null) return null;
    final height = rackUnits;
    if (height == null) return 'Add a rack to the project first.';
    final top = item.topUnit;
    if (top == null) return 'Enter the equipment height to place it.';
    if (bottom < 1 || top > height) {
      return 'Choose a position within U1–U$rackUnits.';
    }
    for (final other in equipment) {
      final otherBottom = other.bottomUnit;
      final otherTop = other.topUnit;
      if (identical(other, replacing) ||
          otherBottom == null ||
          otherTop == null ||
          (item.face != other.face && !item.fullDepth && !other.fullDepth)) {
        continue;
      }
      if (bottom <= otherTop && otherBottom <= top) {
        return 'This position overlaps ${other.name}.';
      }
    }
    return null;
  }

  int get highestOccupiedUnit => equipment.fold(
    0,
    (highest, item) => (item.topUnit ?? 0) > highest ? item.topUnit! : highest,
  );

  int get occupiedUnits => {
    for (var unit = 1; unit <= (rackUnits ?? 0) && unit <= 60; unit++)
      if (equipment.any(
        (item) =>
            item.bottomUnit != null &&
            item.topUnit != null &&
            item.bottomUnit! <= unit &&
            item.topUnit! >= unit,
      ))
        unit,
  }.length;
}

class EquipmentDraft {
  EquipmentDraft({
    required this.name,
    this.heightUnits,
    this.bottomUnit,
    this.placedTopUnit,
    this.depthMm,
    this.weightKg,
    this.powerW,
    this.monitorId,
    this.source,
    String? id,
  }) : id = id ?? source?.id ?? newModelId();

  factory EquipmentDraft.fromPlacement(Placement placement) => EquipmentDraft(
    name: placement.name,
    bottomUnit: placement.u?.$1,
    placedTopUnit: placement.u?.$2,
    heightUnits: placement.units,
    depthMm: placement.depth?.base('length'),
    weightKg: placement.weight?.base('mass'),
    powerW: placement.power?.base('power'),
    monitorId: placement.thing,
    source: placement,
  );

  final String name;
  final String id;
  final int? heightUnits;
  final int? bottomUnit;
  final int? placedTopUnit;
  final double? depthMm;
  final double? weightKg;
  final double? powerW;
  final String? monitorId;
  final Placement? source;
  String get face => source?.face ?? 'front';
  bool get fullDepth => source?.fullDepth ?? false;

  Placement toPlacement() => Placement(
    id: id,
    name: name,
    item: source?.item,
    thing: monitorId,
    u: bottomUnit == null || topUnit == null ? null : (bottomUnit!, topUnit!),
    height: heightUnits,
    face: face,
    fullDepth: fullDepth,
    depth: _quantity(source?.depth, depthMm, 'length', 'mm'),
    weight: _quantity(source?.weight, weightKg, 'mass', 'kg'),
    power: _quantity(source?.power, powerW, 'power', 'W'),
    extra: source?.extra,
  );

  int? get topUnit =>
      placedTopUnit ??
      (bottomUnit == null || heightUnits == null
          ? null
          : bottomUnit! + heightUnits! - 1);

  String get placement {
    final bottom = bottomUnit;
    final top = topUnit;
    if (bottom == null || top == null) return 'Unplaced';
    return top == bottom ? 'U$bottom' : 'U$bottom–U$top';
  }
}

class ProjectTaskDraft {
  ProjectTaskDraft(this.title, {String? id, this.done = false})
    : id = id ?? newModelId();

  final String id;
  final String title;
  bool done;
}

Qty? _quantity(Qty? original, double? value, String kind, String unit) =>
    original?.base(kind) == value
    ? original
    : (value == null ? null : Qty(value, unit));
