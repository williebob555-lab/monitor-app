import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';
import 'package:url_launcher/url_launcher.dart';

import 'theme.dart';
import 'widgets.dart';

class ProductsPage extends StatefulWidget {
  const ProductsPage(this.products, this.inventory, {super.key});
  final ProductStore products;
  final ModelStore inventory;

  @override
  State<ProductsPage> createState() => _ProductsPageState();
}

class _ProductsPageState extends State<ProductsPage> {
  List<Product> _products = [];
  bool _unavailable = false;
  String _query = '';

  @override
  void initState() {
    super.initState();
    _read();
  }

  void _read() {
    try {
      _products = widget.products.list()
        ..sort((a, b) => a.name.toLowerCase().compareTo(b.name.toLowerCase()));
      _unavailable = false;
    } on Exception {
      _unavailable = true;
    }
  }

  Future<void> _edit([Product? prior]) async {
    final form = GlobalKey<FormState>();
    var name = prior?.name ?? '';
    var maker = prior?.maker ?? '';
    var model = prior?.model ?? '';
    var manual = '';
    final save = await showDialog<bool>(
      context: context,
      builder: (context) => AlertDialog(
        title: Text(prior == null ? 'Add a product' : 'Edit product'),
        scrollable: true,
        content: Form(
          key: form,
          child: Column(
            mainAxisSize: MainAxisSize.min,
            children: [
              TextFormField(
                initialValue: name,
                decoration: const InputDecoration(labelText: 'Product name'),
                maxLength: 120,
                validator: (value) => (value?.trim() ?? '').isEmpty
                    ? 'Enter a product name.'
                    : null,
                onChanged: (value) => name = value.trim(),
              ),
              TextFormField(
                initialValue: maker,
                maxLength: 120,
                decoration: const InputDecoration(labelText: 'Manufacturer'),
                onChanged: (value) => maker = value.trim(),
              ),
              TextFormField(
                initialValue: model,
                maxLength: 120,
                decoration: const InputDecoration(labelText: 'Exact model'),
                onChanged: (value) => model = value.trim(),
              ),
              TextFormField(
                decoration: const InputDecoration(
                  labelText: 'Add a manual link',
                ),
                maxLength: 1500,
                keyboardType: TextInputType.url,
                onChanged: (value) => manual = value.trim(),
                validator: (value) => manual.isNotEmpty && _web(manual) == null
                    ? 'Enter an HTTP or HTTPS link.'
                    : null,
              ),
            ],
          ),
        ),
        actions: [
          TextButton(
            onPressed: () => Navigator.pop(context),
            child: const Text('Cancel'),
          ),
          FilledButton(
            onPressed: () {
              if (form.currentState!.validate()) Navigator.pop(context, true);
            },
            child: const Text('Save product'),
          ),
        ],
      ),
    );
    if (save != true || !mounted) return;
    try {
      final current = prior == null
          ? Product(id: newModelId(), name: name)
          : widget.products.get(prior.id);
      if (current == null ||
          prior != null &&
              (current.state == 'retired' ||
                  newerVersion(current, prior) != 0)) {
        _message('This product changed elsewhere. Open its latest version.');
        setState(_read);
        return;
      }
      current
        ..name = name
        ..maker = maker.isEmpty ? null : maker
        ..model = model.isEmpty ? null : model;
      if (manual.isNotEmpty &&
          !current.docs.any((doc) => doc['url'] == manual)) {
        current.docs.add({'title': 'Manual', 'url': manual});
      }
      widget.products.put(current);
      setState(_read);
    } on Exception {
      _message('Could not save this product.');
    }
  }

  Future<void> _link(Product product) async {
    try {
      final items = widget.inventory.list()
        ..sort((a, b) => a.name.toLowerCase().compareTo(b.name.toLowerCase()));
      final choice = await showDialog<Item>(
        context: context,
        builder: (context) => SimpleDialog(
          title: Text('Link ${product.name} to inventory'),
          children: [
            if (items.isEmpty)
              const SimpleDialogOption(
                child: Text('There are no inventory items yet.'),
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
      if (choice == null || !mounted) return;
      final previous = widget.products.of(choice);
      if (previous?.id == product.id) {
        _message('${choice.name} already links to this product.');
        return;
      }
      if (choice.extra['product'] != null) {
        final confirmed = await showDialog<bool>(
          context: context,
          builder: (context) => AlertDialog(
            title: const Text('Replace the product link?'),
            content: Text('${choice.name} will link to ${product.name}.'),
            actions: [
              TextButton(
                onPressed: () => Navigator.pop(context),
                child: const Text('Cancel'),
              ),
              FilledButton(
                onPressed: () => Navigator.pop(context, true),
                child: const Text('Replace link'),
              ),
            ],
          ),
        );
        if (!mounted || confirmed != true) return;
      }
      final item = widget.inventory.get(choice.id);
      if (item == null ||
          item.state == 'retired' ||
          item.extra['product'] != choice.extra['product'] ||
          widget.products.get(product.id)?.state != 'active') {
        _message('The item or product changed. Reopen it and try again.');
        return;
      }
      widget.products.link(widget.inventory, item, product.id);
      _message('Linked ${item.name} to ${product.name}.');
    } on Exception {
      if (mounted) _message('Could not link this product to inventory.');
    }
  }

  void _message(String message) =>
      ScaffoldMessenger.of(context)
          .showSnackBar(SnackBar(content: Text(message)));

  @override
  Widget build(BuildContext context) {
    final visible = _products.where(
      (product) => [
        product.name,
        product.maker,
        product.model,
      ].whereType<String>().join(' ').toLowerCase().contains(_query),
    );
    return Scaffold(
      appBar: AppBar(title: const Text('Products')),
      body: Center(
        child: ConstrainedBox(
          constraints: const BoxConstraints(maxWidth: 740),
          child: ListView(
            padding: const EdgeInsets.all(14),
            children: [
              TextField(
                decoration: const InputDecoration(
                  labelText: 'Find a product',
                  prefixIcon: Icon(Icons.search),
                ),
                onChanged: (value) =>
                    setState(() => _query = value.trim().toLowerCase()),
              ),
              const SizedBox(height: 10),
              Align(
                alignment: Alignment.centerLeft,
                child: FilledButton.icon(
                  onPressed: _edit,
                  icon: const Icon(Icons.add),
                  label: const Text('Add a product'),
                ),
              ),
              const SizedBox(height: 10),
              if (_unavailable)
                const Text('Could not read saved products.')
              else if (visible.isEmpty)
                Text(
                  _query.isEmpty
                      ? 'No products saved.'
                      : 'No matching products.',
                ),
              for (final product in visible)
                _ProductCard(
                  product,
                  onEdit: () => _edit(product),
                  onLink: () => _link(product),
                  open: (url) async {
                    try {
                      if (await launchUrl(
                        url,
                        mode: LaunchMode.externalApplication,
                      )) {
                        return;
                      }
                    } on Exception {
                      // Show one message for a failed launch.
                    }
                    if (mounted) _message('Could not open this link.');
                  },
                ),
            ],
          ),
        ),
      ),
    );
  }
}

class _ProductCard extends StatelessWidget {
  const _ProductCard(
    this.product, {
    required this.onEdit,
    required this.onLink,
    required this.open,
  });
  final Product product;
  final VoidCallback onEdit, onLink;
  final ValueChanged<Uri> open;

  @override
  Widget build(BuildContext context) {
    final identity = [
      product.maker,
      product.model,
    ].whereType<String>().join(' · ');
    return Card(
      child: ExpansionTile(
        key: PageStorageKey(product.id),
        title: Text(product.name),
        subtitle: identity.isEmpty ? null : Text(identity),
        childrenPadding: const EdgeInsets.fromLTRB(12, 0, 12, 10),
        children: [
          Wrap(
            spacing: 8,
            children: [
              TextButton(onPressed: onEdit, child: const Text('Edit product')),
              TextButton(
                onPressed: onLink,
                child: const Text('Link inventory item'),
              ),
            ],
          ),
          if (product.specs.isNotEmpty)
            Section(
              'Specifications',
              Column(
                children: [
                  for (final entry in product.specs.entries)
                    ListTile(
                      title: Text(entry.key),
                      subtitle: Text(
                        [
                          [
                            entry.value.value?.toString() ?? 'Not available',
                            if (entry.value.value != null) ?entry.value.unit,
                          ].join(' '),
                          entry.value.src,
                        ].join(' · '),
                      ),
                    ),
                ],
              ),
            ),
          for (final doc in product.docs)
            if (_web(doc['url']) case final url?)
              ListTile(
                title: Text('${doc['title'] ?? 'Document'}'),
                trailing: const Icon(Icons.open_in_new, size: 16),
                onTap: () => open(url),
              ),
          for (final (index, image) in product.images.indexed)
            if (_web(image) case final url?)
              ListTile(
                title: Text('Open product photo ${index + 1}'),
                trailing: const Icon(Icons.open_in_new, size: 16),
                onTap: () => open(url),
              ),
          for (final offer in product.offers)
            ListTile(
              title: Text(offer.seller),
              subtitle: Text(
                [
                  if (offer.price != null)
                    [
                      offer.price!.toStringAsFixed(2),
                      ?offer.currency,
                    ].join(' '),
                  switch (offer.inStock) {
                    true => 'In stock',
                    false => 'Out of stock',
                    null => 'Stock unknown',
                  },
                ].join(' · '),
                style: const TextStyle(color: C.muted),
              ),
              onTap: switch (_web(offer.url)) {
                final Uri url => () => open(url),
                _ => null,
              },
            ),
        ],
      ),
    );
  }
}

Uri? _web(Object? value) {
  if (value is! String) return null;
  final uri = Uri.tryParse(value);
  if (uri == null ||
      !const {'http', 'https'}.contains(uri.scheme) ||
      uri.host.isEmpty ||
      uri.userInfo.isNotEmpty) {
    return null;
  }
  return uri;
}
