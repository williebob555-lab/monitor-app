/// Editable UI drafts for the current app session. Planning storage belongs
/// to monitor_core once the shared planning contract is available.
class ProjectDraft {
  ProjectDraft(this.name, this.rackUnits, {this.rackDepthMm, this.goal = ''});

  String name;
  String goal;
  int rackUnits;
  double? rackDepthMm;
  final equipment = <EquipmentDraft>[];
  final tasks = <ProjectTaskDraft>[];

  String? placementError(EquipmentDraft item, {EquipmentDraft? replacing}) {
    final bottom = item.bottomUnit;
    if (bottom == null) return null;
    if (bottom < 1 || item.topUnit! > rackUnits) {
      return 'Choose a position within U1–U$rackUnits.';
    }
    for (final other in equipment) {
      final otherBottom = other.bottomUnit;
      if (identical(other, replacing) || otherBottom == null) continue;
      if (bottom <= other.topUnit! && otherBottom <= item.topUnit!) {
        return 'This position overlaps ${other.name}.';
      }
    }
    return null;
  }

  int get highestOccupiedUnit => equipment.fold(
    0,
    (highest, item) => (item.topUnit ?? 0) > highest ? item.topUnit! : highest,
  );

  int get occupiedUnits => equipment
      .where((item) => item.bottomUnit != null)
      .fold(0, (total, item) => total + item.heightUnits);
}

class EquipmentDraft {
  EquipmentDraft({
    required this.name,
    required this.heightUnits,
    this.bottomUnit,
    this.depthMm,
    this.weightKg,
    this.powerW,
  });

  final String name;
  final int heightUnits;
  final int? bottomUnit;
  final double? depthMm;
  final double? weightKg;
  final double? powerW;

  int? get topUnit => bottomUnit == null ? null : bottomUnit! + heightUnits - 1;

  String get placement {
    final bottom = bottomUnit;
    if (bottom == null) return 'Unplaced';
    return heightUnits == 1 ? 'U$bottom' : 'U$bottom–U$topUnit';
  }
}

class ProjectTaskDraft {
  ProjectTaskDraft(this.title);

  final String title;
  bool done = false;
}
