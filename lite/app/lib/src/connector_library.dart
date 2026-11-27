import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';

import 'theme.dart';

String connectorFamily(String family) => switch (family) {
  'usb' => 'USB',
  'rf' => 'RF',
  'display' => 'Display',
  'network' => 'Network',
  'power' => 'Power',
  'audio' => 'Audio',
  'eurorack' => 'Eurorack',
  'storage' => 'Storage',
  'control' => 'Control',
  _ => family,
};

Future<ConnectorDef?> chooseConnector(
  BuildContext context,
  Library library,
  String title,
) => showModalBottomSheet<ConnectorDef>(
  context: context,
  useSafeArea: true,
  isScrollControlled: true,
  builder: (context) => Padding(
    padding: EdgeInsets.only(bottom: MediaQuery.viewInsetsOf(context).bottom),
    child: FractionallySizedBox(
      heightFactor: 0.82,
      child: Column(
        children: [
          Padding(
            padding: const EdgeInsets.fromLTRB(14, 14, 14, 4),
            child: Text(title, style: Theme.of(context).textTheme.titleMedium),
          ),
          Expanded(
            child: ConnectorList(
              library,
              onOpen: (connector) => Navigator.of(context).pop(connector),
            ),
          ),
        ],
      ),
    ),
  ),
);

class ConnectorList extends StatefulWidget {
  const ConnectorList(this.library, {super.key, required this.onOpen});
  final Library library;
  final ValueChanged<ConnectorDef> onOpen;

  @override
  State<ConnectorList> createState() => _ConnectorListState();
}

class _ConnectorListState extends State<ConnectorList> {
  String _query = '';

  @override
  Widget build(BuildContext context) {
    final connectors =
        widget.library.connectors.values
            .where(
              (c) => '${c.label} ${c.id} ${connectorFamily(c.family)}'
                  .toLowerCase()
                  .contains(_query),
            )
            .toList()
          ..sort((a, b) => a.label.compareTo(b.label));
    final groups = <String, List<ConnectorDef>>{};
    for (final connector in connectors) {
      groups.putIfAbsent(connector.family, () => []).add(connector);
    }
    final families = groups.keys.toList()
      ..sort((a, b) => connectorFamily(a).compareTo(connectorFamily(b)));
    return Column(
      children: [
        Padding(
          padding: const EdgeInsets.all(14),
          child: TextField(
            decoration: const InputDecoration(
              labelText: 'Find a connector',
              prefixIcon: Icon(Icons.search),
              border: OutlineInputBorder(),
            ),
            onChanged: (value) =>
                setState(() => _query = value.trim().toLowerCase()),
          ),
        ),
        Expanded(
          child: connectors.isEmpty
              ? const Center(child: Text('No matching connectors.'))
              : ListView(
                  padding: const EdgeInsets.fromLTRB(14, 0, 14, 20),
                  children: [
                    for (final family in families)
                      ExpansionTile(
                        key: ValueKey((family, _query.isNotEmpty)),
                        title: Text(connectorFamily(family)),
                        initiallyExpanded: _query.isNotEmpty,
                        children: [
                          for (final connector in groups[family]!)
                            ListTile(
                              title: Text(connector.label),
                              subtitle: connector.note == null
                                  ? null
                                  : Text(
                                      connector.note!,
                                      maxLines: 2,
                                      overflow: TextOverflow.ellipsis,
                                    ),
                              onTap: () => widget.onOpen(connector),
                            ),
                        ],
                      ),
                  ],
                ),
        ),
      ],
    );
  }
}

class ConnectorDetails extends StatelessWidget {
  const ConnectorDetails(this.connector, this.library, {super.key});
  final ConnectorDef connector;
  final Library library;

  @override
  Widget build(BuildContext context) => Scaffold(
    appBar: AppBar(title: Text(connector.label)),
    body: ListView(
      padding: const EdgeInsets.all(14),
      children: [
        if (connector.note case final String note)
          Text(note, style: const TextStyle(color: C.muted)),
        if (connector.pins.isNotEmpty) ...[
          const SizedBox(height: 14),
          const Text('Pinout', style: TextStyle(fontWeight: FontWeight.w600)),
          for (final pin in connector.pins.entries)
            ListTile(title: Text(pin.key), subtitle: Text(pin.value)),
        ],
        if (connector.signals.isNotEmpty) ...[
          const SizedBox(height: 14),
          const Text(
            'Signals this shape may carry',
            style: TextStyle(fontWeight: FontWeight.w600),
          ),
          for (final id in connector.signals)
            ListTile(
              title: Text(library.signals[id]?.label ?? id),
              subtitle: Text(library.signals[id]?.note ?? ''),
            ),
        ],
      ],
    ),
  );
}
