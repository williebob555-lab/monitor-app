import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';

import 'host.dart';

class PhoneLocation extends StatefulWidget {
  const PhoneLocation(this.host, {super.key});
  final LocalHost host;

  @override
  State<PhoneLocation> createState() => _PhoneLocationState();
}

class _PhoneLocationState extends State<PhoneLocation>
    with WidgetsBindingObserver {
  ({bool on, bool granted})? _status;
  bool _busy = false;
  bool _error = false;
  int _request = 0;

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    _refresh();
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    super.dispose();
  }

  @override
  void didChangeAppLifecycleState(AppLifecycleState state) {
    if (state == AppLifecycleState.resumed) _refresh();
  }

  Future<void> _refresh() async {
    final request = ++_request;
    try {
      final latest = await widget.host.locationSharing();
      if (mounted && request == _request) {
        setState(() {
          _status = latest;
          _error = false;
        });
      }
    } on Exception {
      if (mounted && request == _request) setState(() => _error = true);
    }
  }

  Future<void> _change(bool on) async {
    setState(() => _busy = true);
    try {
      if (on) {
        final allow = await showDialog<bool>(
          context: context,
          builder: (context) => AlertDialog(
            title: const Text('Share this phone’s approximate location?'),
            content: const Text(
              'Every device with your Monitor folder can read the phone’s last known location, rounded to about 1 km.',
            ),
            actions: [
              TextButton(
                onPressed: () => Navigator.pop(context),
                child: const Text('Cancel'),
              ),
              FilledButton(
                onPressed: () => Navigator.pop(context, true),
                child: const Text('Share location'),
              ),
            ],
          ),
        );
        if (allow != true || !mounted) return;
      }
      await widget.host.setLocationSharing(on);
      if (!on) await widget.host.nudge();
      await _refresh();
    } on Exception {
      if (mounted) setState(() => _error = true);
    } finally {
      if (mounted) setState(() => _busy = false);
    }
  }

  @override
  Widget build(BuildContext context) {
    final status = _status;
    final message = _error
        ? 'Could not read or change location sharing.'
        : status == null
        ? 'Reading this phone’s preference…'
        : status.on && !status.granted
        ? 'Android location permission is required before anything is shared.'
        : 'Shares the last known position with devices in your Monitor folder.';
    return Card(
      child: Column(
        crossAxisAlignment: CrossAxisAlignment.start,
        children: [
          SwitchListTile(
            title: const Text('Share approximate location'),
            subtitle: Text(message),
            value: status?.on ?? false,
            onChanged: _busy || status == null ? null : _change,
          ),
          if (_error || status?.on == true && status?.granted == false)
            Padding(
              padding: const EdgeInsets.only(left: 12),
              child: TextButton(
                onPressed: _busy ? null : _refresh,
                child: const Text('Check location permission'),
              ),
            ),
        ],
      ),
    );
  }
}

String? reportedLocation(Reading reading) {
  if (reading.key != 'loc' || reading.value is! String) return null;
  final parts = (reading.value as String).split(',');
  if (parts.length != 2) return null;
  final lat = double.tryParse(parts.first);
  final lon = double.tryParse(parts.last);
  if (lat == null ||
      !lat.isFinite ||
      lat.abs() > 90 ||
      lon == null ||
      !lon.isFinite ||
      lon.abs() > 180) {
    return null;
  }
  return [
    '${lat.abs().toStringAsFixed(2)}° ${lat < 0 ? 'S' : 'N'}',
    '${lon.abs().toStringAsFixed(2)}° ${lon < 0 ? 'W' : 'E'}',
    if (reading.note case final String note) note,
  ].join(' · ');
}
