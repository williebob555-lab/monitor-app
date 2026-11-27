import 'package:monitor_core/monitor_core.dart';

/// dart run tool/suggest.dart <folder>: what could become inventory items.
void main(List<String> args) {
  final f = SyncFolder(args.first);
  final store = ModelStore(args.first, 'tool');
  final items = [for (final id in store.ids()) store.get(id)].whereType<Item>().toList();
  for (final s in suggestLinks(f.readReports(), f.readThings(), items, DateTime.now().toUtc())) {
    print('${s.relinkItem != null ? 'RELINK ${s.relinkItem} ' : ''}${s.type.padRight(9)} ${s.name}  observes=${s.observes}${s.also.isEmpty ? '' : ' also=${s.also.join(',')}'}'
        '${s.parentObserves != null ? ' under=${s.parentObserves}' : ''}  match=${s.match}  ${s.props.entries.map((e) => '${e.key}=${e.value.value}').join(' ')}');
  }
}
