import 'package:flutter/foundation.dart';
import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';

import 'connector_library.dart';
import 'theme.dart';
import 'widgets.dart';

class ConnectionsPage extends StatefulWidget {
  const ConnectionsPage(this.root, {super.key});
  final String root;

  @override
  State<ConnectionsPage> createState() => _ConnectionsPageState();
}

class _ConnectionsPageState extends State<ConnectionsPage> {
  late final _library = compute(Library.load, widget.root);

  @override
  Widget build(BuildContext context) => FutureBuilder<Library>(
    future: _library,
    builder: (context, snapshot) {
      final library = snapshot.data;
      if (library == null) {
        return Scaffold(
          appBar: AppBar(title: const Text('Connections')),
          body: Center(
            child: snapshot.hasError
                ? const Text('Could not open the connector library.')
                : const CircularProgressIndicator(),
          ),
        );
      }
      return DefaultTabController(
        length: 2,
        child: Scaffold(
          appBar: AppBar(
            title: const Text('Connections'),
            bottom: const TabBar(
              tabs: [
                Tab(text: 'Connector library'),
                Tab(text: 'Check a connection'),
              ],
            ),
          ),
          body: TabBarView(
            children: [
              Column(
                children: [
                  if (library.problems.isNotEmpty)
                    ListTile(
                      title: const Text(
                        'Some custom entries could not be read',
                      ),
                      trailing: const Icon(Icons.info_outline),
                      onTap: () => showDialog<void>(
                        context: context,
                        builder: (context) => AlertDialog(
                          title: const Text('Custom connector entries'),
                          scrollable: true,
                          content: Text(
                            library.problems.entries
                                .map((e) => '${e.key} · ${e.value}')
                                .join('\n'),
                          ),
                          actions: [
                            TextButton(
                              onPressed: () => Navigator.of(context).pop(),
                              child: const Text('Close'),
                            ),
                          ],
                        ),
                      ),
                    ),
                  Expanded(
                    child: ConnectorList(
                      library,
                      onOpen: (connector) {
                        Navigator.of(context).push<void>(
                          MaterialPageRoute(
                            builder: (_) =>
                                ConnectorDetails(connector, library),
                          ),
                        );
                      },
                    ),
                  ),
                ],
              ),
              ConnectionCheck(library),
            ],
          ),
        ),
      );
    },
  );
}

class ConnectionCheck extends StatefulWidget {
  const ConnectionCheck(this.library, {super.key});
  final Library library;

  @override
  State<ConnectionCheck> createState() => _ConnectionCheckState();
}

class _ConnectionCheckState extends State<ConnectionCheck>
    with AutomaticKeepAliveClientMixin {
  final _source = _End('Source port', 'receptacle');
  final _destination = _End('Destination port', 'receptacle');
  final _links = <(_End, _End)>[];
  ChainVerdict? _result;

  @override
  bool get wantKeepAlive => true;

  void _change(VoidCallback action) => setState(() {
    action();
    _result = null;
  });

  List<String> _genders(ConnectorDef connector) {
    final options = connector.genders
        .where((g) => const {'plug', 'receptacle', 'genderless'}.contains(g))
        .toList();
    return options.isEmpty ? ['plug', 'receptacle'] : options;
  }

  Future<void> _choose(_End end) async {
    final connector = await chooseConnector(context, widget.library, end.name);
    if (connector == null || !mounted) return;
    _change(() {
      end.connector = connector;
      final genders = _genders(connector);
      if (!genders.contains(end.gender)) end.gender = genders.first;
    });
  }

  Widget _end(_End end) {
    final connector = end.connector;
    return Column(
      crossAxisAlignment: CrossAxisAlignment.start,
      children: [
        Text(end.name, style: const TextStyle(color: C.muted)),
        Wrap(
          spacing: 10,
          crossAxisAlignment: WrapCrossAlignment.center,
          children: [
            OutlinedButton(
              onPressed: () => _choose(end),
              child: Text(connector?.label ?? 'Choose connector'),
            ),
            if (connector != null)
              DropdownButton<String>(
                value: end.gender,
                onChanged: (value) {
                  if (value != null) _change(() => end.gender = value);
                },
                items: [
                  for (final g in _genders(connector))
                    DropdownMenuItem(
                      value: g,
                      child: Text(switch (g) {
                        'plug' => 'Plug',
                        'receptacle' => 'Socket',
                        _ => 'Genderless',
                      }),
                    ),
                ],
              ),
          ],
        ),
      ],
    );
  }

  void _check() {
    final result = checkChain(
      Port(
        _source.name,
        _source.connector!.id,
        gender: _source.gender,
        dir: 'out',
      ),
      [
        for (final (index, (a, b)) in _links.indexed)
          Link(
            'Link ${index + 1}',
            a.connector!.id,
            b.connector!.id,
            aGender: a.gender,
            bGender: b.gender,
          ),
      ],
      Port(
        _destination.name,
        _destination.connector!.id,
        gender: _destination.gender,
        dir: 'in',
      ),
      library: widget.library,
    );
    setState(() => _result = result);
  }

  @override
  Widget build(BuildContext context) {
    super.build(context);
    final ready =
        _source.connector != null &&
        _destination.connector != null &&
        _links.every(
          (link) => link.$1.connector != null && link.$2.connector != null,
        );
    return Center(
      child: ConstrainedBox(
        constraints: const BoxConstraints(maxWidth: 740),
        child: ListView(
          padding: const EdgeInsets.all(14),
          children: [
            _end(_source),
            for (final (index, (a, b)) in _links.indexed)
              Card(
                child: Padding(
                  padding: const EdgeInsets.all(12),
                  child: Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Row(
                        children: [
                          Expanded(
                            child: Text('Cable or adapter ${index + 1}'),
                          ),
                          IconButton(
                            tooltip: 'Remove link ${index + 1}',
                            onPressed: () =>
                                _change(() => _links.removeAt(index)),
                            icon: const Icon(Icons.close),
                          ),
                        ],
                      ),
                      _end(a),
                      _end(b),
                    ],
                  ),
                ),
              ),
            Align(
              alignment: Alignment.centerLeft,
              child: TextButton.icon(
                onPressed: () => _change(
                  () => _links.add((
                    _End('Source end', 'plug'),
                    _End('Destination end', 'plug'),
                  )),
                ),
                icon: const Icon(Icons.add),
                label: const Text('Add cable or adapter'),
              ),
            ),
            _end(_destination),
            const SizedBox(height: 12),
            Align(
              alignment: Alignment.centerLeft,
              child: FilledButton(
                onPressed: ready ? _check : null,
                child: const Text('Check connection'),
              ),
            ),
            if (_result case final result?) ...[
              const SizedBox(height: 12),
              const Text(
                'Based on the selected connector shapes and library references.',
                style: TextStyle(color: C.muted),
              ),
              const SizedBox(height: 8),
              for (final layer in result.layers)
                Section(
                  _layer(layer.layer),
                  Column(
                    crossAxisAlignment: CrossAxisAlignment.start,
                    children: [
                      Text(
                        '${_verdict(layer.verdict)} · ${layer.evidence}',
                        style: TextStyle(
                          color: layer.verdict == 'fail' ? C.warn : C.text,
                          fontWeight: FontWeight.w600,
                        ),
                      ),
                      for (final reason in layer.reasons) Text(reason),
                    ],
                  ),
                ),
            ],
          ],
        ),
      ),
    );
  }
}

class _End {
  _End(this.name, this.gender);
  final String name;
  String gender;
  ConnectorDef? connector;
}

String _layer(String layer) => switch (layer) {
  'physical' => 'Physical fit',
  'protocol' => 'Protocol',
  'electrical' => 'Electrical',
  'level' => 'Audio level',
  _ => layer,
};

String _verdict(String verdict) => switch (verdict) {
  'ok' => 'Pass',
  'fail' => 'Mismatch',
  _ => 'Unknown',
};
