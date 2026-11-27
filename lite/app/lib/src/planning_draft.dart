/// Editable UI drafts for the current app session. Planning storage belongs
/// to monitor_core once the shared planning contract is available.
class ProjectDraft {
  ProjectDraft(this.name, {this.rackUnits, this.rackDepthMm, this.goal = ''});

  String name;
  String goal;
  int? rackUnits;
  double? rackDepthMm;
  final equipment = <EquipmentDraft>[];
  final tasks = <ProjectTaskDraft>[];

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
          otherTop == null) {
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

  int get occupiedUnits => equipment
      .where((item) => item.bottomUnit != null)
      .fold(0, (total, item) => total + (item.heightUnits ?? 0));
}

class EquipmentDraft {
  EquipmentDraft({
    required this.name,
    this.heightUnits,
    this.bottomUnit,
    this.depthMm,
    this.weightKg,
    this.powerW,
    this.monitorId,
  });

  final String name;
  final int? heightUnits;
  final int? bottomUnit;
  final double? depthMm;
  final double? weightKg;
  final double? powerW;
  final String? monitorId;

  int? get topUnit => bottomUnit == null || heightUnits == null
      ? null
      : bottomUnit! + heightUnits! - 1;

  String get placement {
    final bottom = bottomUnit;
    if (bottom == null || heightUnits == null) return 'Unplaced';
    return heightUnits == 1 ? 'U$bottom' : 'U$bottom–U$topUnit';
  }
}

class ProjectTaskDraft {
  ProjectTaskDraft(this.title);

  final String title;
  bool done = false;
}
