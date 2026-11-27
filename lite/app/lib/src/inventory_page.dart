import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';

import 'theme.dart';

class InventoryPage extends StatefulWidget {
  const InventoryPage(this.store, {super.key});

  final ModelStore store;

  @override
  State<InventoryPage> createState() => _InventoryPageState();
}

class _InventoryPageState extends State<InventoryPage> {
  List<Item> items = [];
  bool unavailable = false;
  String query = '';

  static const kinds = {
    'thing': 'Thing',
    'device': 'Device',
    'rack': 'Rack',
    'part': 'Part',
    'cable': 'Cable',
  };
  static const states = {
    'proposed': 'Proposed',
    'owned': 'Owned',
    'installed': 'Installed',
  };

  @override
  void initState() {
    super.initState();
    _read();
  }

  void _read() {
    try {
      items = widget.store.list()
        ..sort((a, b) => a.name.toLowerCase().compareTo(b.name.toLowerCase()));
      unavailable = false;
    } catch (_) {
      unavailable = true;
    }
  }

  Future<void> _edit([Item? item]) async {
    final form = GlobalKey<FormState>();
    var name = item?.name ?? '';
    var kind = item?.type ?? 'thing';
    var lifecycle = item?.state ?? 'proposed';
    final kindOptions = {if (!kinds.containsKey(kind)) kind: kind, ...kinds};
    final stateOptions = {
      if (!states.containsKey(lifecycle)) lifecycle: lifecycle,
      ...states,
    };
    final confirmed = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: Text(item == null ? 'Add a thing' : 'Edit thing'),
        scrollable: true,
        content: Form(
          key: form,
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              TextFormField(
                initialValue: name,
                maxLength: 100,
                decoration: const InputDecoration(labelText: 'Name'),
                onChanged: (value) => name = value,
                validator: (value) =>
                    value?.trim().isNotEmpty == true ? null : 'Enter a name.',
              ),
              _selector('Kind', kind, kindOptions, (value) => kind = value),
              _selector(
                'State',
                lifecycle,
                stateOptions,
                (value) => lifecycle = value,
              ),
            ],
          ),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context, false),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () {
              if (form.currentState!.validate()) Navigator.pop(context, true);
            },
            child: const Text('Save'),
          ),
        ],
      ),
    );
    if (confirmed != true || !mounted) return;
    try {
      final saved = item == null
          ? Item(
              id: newModelId(),
              type: kind,
              name: name.trim(),
              state: lifecycle,
            )
          : widget.store.get(item.id);
      if (saved == null || (item != null && saved.state == 'retired')) {
        setState(_read);
        ScaffoldMessenger.of(context).showSnackBar(
          const SnackBar(content: Text('The item changed elsewhere.')),
        );
        return;
      }
      if (item != null) {
        if (name.trim() != item.name) saved.name = name.trim();
        if (kind != item.type) saved.type = kind;
        if (lifecycle != item.state) saved.state = lifecycle;
      }
      widget.store.put(saved);
      setState(_read);
    } catch (_) {
      ScaffoldMessenger.of(context).showSnackBar(
        const SnackBar(content: Text('Could not save to the Monitor folder.')),
      );
    }
  }

  Widget _selector(
    String label,
    String initial,
    Map<String, String> options,
    ValueChanged<String> setValue,
  ) => DropdownButtonFormField<String>(
    initialValue: initial,
    decoration: InputDecoration(labelText: label),
    items: [
      for (final option in options.entries)
        DropdownMenuItem(value: option.key, child: Text(option.value)),
    ],
    onChanged: (value) {
      if (value != null) setValue(value);
    },
  );

  @override
  Widget build(BuildContext context) {
    final visible = items
        .where((item) => item.name.toLowerCase().contains(query))
        .toList();
    return Scaffold(
      appBar: AppBar(
        title: const Text('Inventory'),
        actions: [
          IconButton(
            tooltip: 'Refresh inventory',
            onPressed: () => setState(_read),
            icon: const Icon(Icons.refresh),
          ),
        ],
      ),
      body: Center(
        child: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 740),
          child: ListView(
            padding: const EdgeInsets.fromLTRB(16, 10, 16, 24),
            children: [
              Row(
                children: [
                  Expanded(
                    child: TextField(
                      decoration: const InputDecoration(
                        labelText: 'Find an item',
                        prefixIcon: Icon(Icons.search),
                      ),
                      onChanged: (value) =>
                          setState(() => query = value.trim().toLowerCase()),
                    ),
                  ),
                  const SizedBox(width: 12),
                  FilledButton(
                    onPressed: () => _edit(),
                    child: const Text('Add a thing'),
                  ),
                ],
              ),
              const SizedBox(height: 12),
              if (unavailable)
                const Text(
                  'Could not read the Monitor folder.',
                  style: TextStyle(color: C.warn),
                )
              else if (visible.isEmpty)
                Text(
                  query.isEmpty
                      ? 'No inventory items in the Monitor folder.'
                      : 'No matching items.',
                  style: const TextStyle(color: C.muted),
                ),
              for (final (index, item) in visible.indexed) ...[
                if (index > 0) const Divider(height: 1),
                ListTile(
                  contentPadding: const EdgeInsets.symmetric(horizontal: 4),
                  title: Text(item.name),
                  subtitle: Text(
                    '${kinds[item.type] ?? item.type} · ${states[item.state] ?? item.state}',
                  ),
                  trailing: const Icon(
                    Icons.chevron_right,
                    size: 18,
                    color: C.muted,
                  ),
                  onTap: () => _edit(item),
                ),
              ],
            ],
          ),
        ),
      ),
    );
  }
}
