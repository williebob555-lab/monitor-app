import 'dart:async';

import 'package:flutter/services.dart';
import 'package:material_ui/material_ui.dart';
import 'package:monitor_core/monitor_core.dart';

import 'state.dart';
import 'theme.dart';
import 'widgets.dart';

const syncFolderId = 'monitor-lite';

/// Setup checklist, folder, updates. Every check reads the real state.
class SetupPage extends StatefulWidget {
  const SetupPage(this.state, {super.key});
  final AppState state;
  @override
  State<SetupPage> createState() => _SetupPageState();
}

class _SetupPageState extends State<SetupPage> with WidgetsBindingObserver {
  Map<String, Object?> perms = const {};
  late final TextEditingController folderCtl = TextEditingController(text: widget.state.host.folder);

  @override
  void initState() {
    super.initState();
    WidgetsBinding.instance.addObserver(this);
    _loadPerms();
  }

  @override
  void dispose() {
    WidgetsBinding.instance.removeObserver(this);
    folderCtl.dispose();
    super.dispose();
  }

  // Coming back from Android settings: re-check permissions and collect.
  @override
  void didChangeAppLifecycleState(AppLifecycleState s) {
    if (s == AppLifecycleState.resumed) _loadPerms();
  }

  Future<void> _loadPerms() async {
    final p = await widget.state.host.androidPermissions();
    if (mounted) setState(() => perms = p);
  }

  @override
  Widget build(BuildContext context) {
    final st = widget.state;
    final host = st.host;
    return ListenableBuilder(
      listenable: st,
      builder: (context, _) {
        final me = st.reports[host.id];
        final meStatus = st.assessment.devices.where((d) => d.report.id == host.id).firstOrNull;
        final checks = <_Check>[
          if (host.isAndroid) ...[
            _Check('Allow access to the Monitor folder', perms['allFiles'] == true,
                'Android setting "All files access". Monitor only touches the folder below.', 'Allow', host.openAllFiles),
            _Check('Let Monitor run in the background', perms['battery'] == true,
                'Android setting "Unrestricted" battery. Monitor wakes briefly once a minute (every 5 min with the screen off) and then stops.',
                'Allow', host.openBattery),
            _Check('Syncthing-Fork installed', perms['syncthing'] != null,
                'Syncthing-Fork moves the files between your devices directly, with no computer needed.',
                perms['syncthing'] != null ? 'Open' : null, perms['syncthing'] != null ? host.openSyncthing : null),
          ],
          _Check('Monitor folder exists', st.folder.exists, host.folder),
          _Check('Folder is shared in Syncthing', st.folder.sharedBySyncthing,
              st.folder.syncthingRoot != null && st.folder.syncthingRoot != st.folder.root
                  ? 'Inside the Syncthing folder ${st.folder.syncthingRoot}.'
                  : 'Either put this folder inside a folder Syncthing already shares with your devices (for example Infrastructure/Monitor), or add it in Syncthing with Folder ID "$syncFolderId" on every device.'),
          _Check('This device is collecting', meStatus?.fresh == true,
              me == null ? 'No readings from this device yet.' : 'Last reading ${ago(meStatus!.age)} ago.',
              host.isAndroid ? 'Run now' : 'Start', () async {
            await host.nudge();
            st.refresh(force: true);
          }),
          _Check('Tailscale is on', me?.tailscaleIp != null,
              me?.tailscaleIp != null ? 'Address ${me!.tailscaleIp}' : 'Used only to check whether the other devices are alive.'),
          _Check('Syncthing is running here', me?.syncthingLocal == true,
              'Checked by looking for Syncthing on this device\'s port $syncthingPortText.'),
        ];
        return Scaffold(
          appBar: AppBar(title: const Text('Setup and updates')),
          body: ListView(
            padding: const EdgeInsets.fromLTRB(14, 4, 14, 28),
            children: [
              Center(
                child: ConstrainedBox(
                  constraints: const BoxConstraints(maxWidth: 760),
                  child: Column(children: [
                    _updates(st),
                    const SizedBox(height: 14),
                    Section('This device', Column(children: [for (final c in checks) _checkRow(c)])),
                    const SizedBox(height: 14),
                    _folder(st),
                    const SizedBox(height: 14),
                    _health(st),
                    const SizedBox(height: 14),
                    Section(
                      'How it works',
                      const Text(
                        'Each device writes a small file with its latest readings into the shared Monitor folder, and Syncthing copies it to your other devices. '
                        'Each device also checks the others over Tailscale. If a device\'s file stops updating, Syncthing on it or the path to it is down; '
                        'if it stops answering over Tailscale too, it is off or cut off. Comparing what every device sees tells these apart.',
                        style: TextStyle(color: C.muted, height: 1.45),
                      ),
                    ),
                  ]),
                ),
              ),
            ],
          ),
        );
      },
    );
  }

  Widget _updates(AppState st) {
    final u = st.update;
    return Section(
      'Updates',
      Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
        Text('This device has version ${st.host.version}.', style: const TextStyle(color: C.muted)),
        const SizedBox(height: 10),
        if (u != null) ...[
          Text('Version ${u.version} is available.', style: const TextStyle(fontWeight: FontWeight.w600, fontSize: 16)),
          if (u.notes.trim().isNotEmpty) ...[
            const SizedBox(height: 6),
            Text(u.notes.trim(), maxLines: 12, overflow: TextOverflow.ellipsis, style: const TextStyle(color: C.muted, height: 1.4)),
          ],
          const SizedBox(height: 12),
          if (st.downloadProgress != null)
            LinearProgressIndicator(value: st.downloadProgress == 0 ? null : st.downloadProgress)
          else
            FilledButton.icon(
              onPressed: st.installUpdate,
              icon: const Icon(Icons.system_update_alt),
              label: Text('Install ${u.version}'),
            ),
        ] else
          OutlinedButton(onPressed: st.checking ? null : () => st.checkUpdate(), child: const Text('Check for updates')),
        if (st.updateMessage != null) ...[
          const SizedBox(height: 8),
          Text(st.updateMessage!, style: const TextStyle(color: C.muted, fontSize: 12.5)),
        ],
      ]),
    );
  }

  Widget _folder(AppState st) => Section(
        'Monitor folder',
        Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
          TextField(
            controller: folderCtl,
            style: const TextStyle(fontSize: 14),
            decoration: const InputDecoration(isDense: true, border: OutlineInputBorder()),
          ),
          const SizedBox(height: 10),
          Wrap(spacing: 10, runSpacing: 8, children: [
            FilledButton.tonal(
              onPressed: () async {
                await st.setFolder(folderCtl.text.trim());
                if (mounted) setState(() {});
              },
              child: const Text('Use this folder'),
            ),
            TextButton(
              onPressed: () => Clipboard.setData(ClipboardData(text: folderCtl.text.trim())),
              child: const Text('Copy path'),
            ),
          ]),
          const SizedBox(height: 8),
          Text('Device id ${st.host.id} · Folder ID in Syncthing: $syncFolderId',
              style: const TextStyle(color: C.muted, fontSize: 12)),
        ]),
      );

  Widget _health(AppState st) {
    final h = st.health;
    String short(String p) => p.split(RegExp(r'[\\/]')).reversed.take(2).toList().reversed.join('/');
    return Section(
      'Folder health',
      h.clean
          ? const Text('No leftovers: no conflict copies, no damaged or empty device folders.', style: TextStyle(color: C.muted))
          : Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
              if (h.conflicts.isNotEmpty) ...[
                Text('${h.conflicts.length} Syncthing conflict ${h.conflicts.length == 1 ? 'copy' : 'copies'}. The app ignores them.',
                    style: const TextStyle(fontWeight: FontWeight.w600)),
                const SizedBox(height: 4),
                Text(h.conflicts.take(5).map(short).join('\n'), style: const TextStyle(color: C.muted, fontSize: 12)),
                const SizedBox(height: 8),
                OutlinedButton(
                  onPressed: () {
                    final n = st.removeConflicts();
                    ScaffoldMessenger.of(context).showSnackBar(SnackBar(content: Text('Removed $n conflict copies.')));
                  },
                  child: const Text('Remove conflict copies'),
                ),
                const SizedBox(height: 12),
              ],
              if (h.unreadable.isNotEmpty)
                Text('Could not read the latest file from: ${h.unreadable.join(', ')}. Usually it is still syncing; it is skipped until it reads cleanly.',
                    style: const TextStyle(color: C.muted, fontSize: 12.5)),
              if (h.empty.isNotEmpty)
                Text('Device folders with no readings yet: ${h.empty.join(', ')}.', style: const TextStyle(color: C.muted, fontSize: 12.5)),
            ]),
    );
  }

  Widget _checkRow(_Check c) => Padding(
        padding: const EdgeInsets.only(bottom: 14),
        child: Row(crossAxisAlignment: CrossAxisAlignment.start, children: [
          Icon(c.ok ? Icons.check_circle : Icons.radio_button_unchecked, color: c.ok ? C.ok : C.warn, size: 22),
          const SizedBox(width: 12),
          Expanded(
            child: Column(crossAxisAlignment: CrossAxisAlignment.start, children: [
              Text(c.title, style: const TextStyle(fontWeight: FontWeight.w600)),
              const SizedBox(height: 2),
              Text(c.detail, style: const TextStyle(color: C.muted, fontSize: 12.5, height: 1.35)),
            ]),
          ),
          if (c.action != null && c.onAction != null && !c.ok) ...[
            const SizedBox(width: 8),
            OutlinedButton(onPressed: () => unawaited(c.onAction!()), child: Text(c.action!)),
          ],
        ]),
      );
}

const syncthingPortText = '22000';

class _Check {
  _Check(this.title, this.ok, this.detail, [this.action, this.onAction]);
  final String title;
  final bool ok;
  final String detail;
  final String? action;
  final Future<void> Function()? onAction;
}
