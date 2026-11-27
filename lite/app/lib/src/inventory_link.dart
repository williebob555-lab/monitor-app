import 'package:flutter/foundation.dart';
import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';

LinkSuggestion? inventorySuggestion((String, String, String) request) {
  final (root, writer, subject) = request;
  final folder = SyncFolder(root);
  final store = ModelStore(root, writer);
  return suggestLinks(
    folder.readReports(),
    folder.readThings(),
    store.list(),
    DateTime.now(),
  ).where((s) => s.observes == subject || s.also.contains(subject)).firstOrNull;
}

class InventoryLink extends StatefulWidget {
  const InventoryLink(this.root, this.writer, this.subject, {super.key});
  final String root, writer, subject;

  @override
  State<InventoryLink> createState() => _InventoryLinkState();
}

class _InventoryLinkState extends State<InventoryLink> {
  late var _suggestion = _find();
  bool _saving = false;

  Future<LinkSuggestion?> _find() => compute(inventorySuggestion, (
    widget.root,
    widget.writer,
    widget.subject,
  ));

  Future<void> _accept(LinkSuggestion suggestion) async {
    setState(() => _saving = true);
    try {
      final relink = suggestion.relinkItem != null;
      final confirmed = await showDialog<bool>(
        context: context,
        builder: (context) => AlertDialog(
          title: Text(
            relink ? 'Update the inventory link?' : 'Add to inventory?',
          ),
          content: Text(
            relink
                ? 'Link the existing inventory item to ${suggestion.name}.'
                : 'Add ${suggestion.name} to your owned inventory.',
          ),
          actions: [
            TextButton(
              onPressed: () => Navigator.pop(context),
              child: const Text('Cancel'),
            ),
            FilledButton(
              onPressed: () => Navigator.pop(context, true),
              child: Text(relink ? 'Update link' : 'Add'),
            ),
          ],
        ),
      );
      if (confirmed != true || !mounted) return;
      final latest = await _find();
      if (!mounted) return;
      final message =
          latest != null &&
              latest.match == suggestion.match &&
              latest.relinkItem == suggestion.relinkItem &&
              latest.observes == suggestion.observes
          ? _save(latest)
          : 'The suggested link changed. Review it again.';
      if (mounted) {
        setState(() {
          _suggestion = _find();
        });
        ScaffoldMessenger.of(context)
            .showSnackBar(SnackBar(content: Text(message)));
      }
    } on Exception {
      if (mounted) {
        ScaffoldMessenger.of(context).showSnackBar(
          const SnackBar(content: Text('Could not update the inventory.')),
        );
      }
    } finally {
      if (mounted) setState(() => _saving = false);
    }
  }

  String _save(LinkSuggestion suggestion) {
    final item = acceptLink(ModelStore(widget.root, widget.writer), suggestion);
    return suggestion.relinkItem == null
        ? '${item.name} added to inventory.'
        : 'Updated the link for ${item.name}.';
  }

  @override
  Widget build(BuildContext context) => FutureBuilder<LinkSuggestion?>(
    future: _suggestion,
    builder: (context, snapshot) {
      final suggestion = snapshot.data;
      if (snapshot.connectionState != ConnectionState.done ||
          suggestion == null) {
        return const SizedBox.shrink();
      }
      return Align(
        alignment: Alignment.centerLeft,
        child: TextButton.icon(
          onPressed: _saving ? null : () => _accept(suggestion),
          icon: const Icon(Icons.link),
          label: Text(
            suggestion.relinkItem == null
                ? 'This is mine'
                : 'Review inventory link',
          ),
        ),
      );
    },
  );
}
