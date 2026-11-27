import 'dart:convert';
import 'dart:io';

import 'package:crypto/crypto.dart';

import 'version.dart';

const releaseRepo = 'williebob555-lab/monitor-app';
const releaseTagPrefix = 'lite-v';

class UpdateInfo {
  UpdateInfo(this.version, this.notes, this.assetName, this.assetUrl, this.sha256, this.pageUrl);
  final String version;
  final String notes;
  final String assetName;
  final Uri assetUrl;

  /// From the release's SHA256SUMS file; the download is refused if it differs.
  final String? sha256;
  final String pageUrl;
}

/// Which release file this device installs. [linuxPackage] is "rpm" or "deb".
String assetNameFor(String platform, String version, {String linuxPackage = 'rpm'}) => switch (platform) {
      'android' => 'monitor-app-$version.apk',
      'windows' => 'monitor-app-$version-windows-setup.exe',
      _ => linuxPackage == 'deb' ? 'monitor-app_${version}_amd64.deb' : 'monitor-app-$version-1.x86_64.rpm',
    };

Future<Object?> _getJson(HttpClient c, Uri u) async {
  final req = await c.getUrl(u);
  req.headers.set('Accept', 'application/vnd.github+json');
  req.headers.set('User-Agent', 'monitor-lite');
  final res = await req.close();
  if (res.statusCode != 200) throw HttpException('GitHub answered ${res.statusCode}', uri: u);
  return jsonDecode(await res.transform(utf8.decoder).join());
}

Future<String> _getText(HttpClient c, Uri u) async {
  final req = await c.getUrl(u);
  req.headers.set('User-Agent', 'monitor-lite');
  final res = await req.close();
  if (res.statusCode != 200) throw HttpException('Download answered ${res.statusCode}', uri: u);
  return res.transform(utf8.decoder).join();
}

/// Looks for a newer Monitor Lite release. One small request; called when
/// the app opens and at most every 6 hours while it stays open.
Future<UpdateInfo?> checkForUpdate(String platform, {String linuxPackage = 'rpm', String current = appVersion}) async {
  final c = HttpClient()..connectionTimeout = const Duration(seconds: 15);
  try {
    final list = await _getJson(c, Uri.parse('https://api.github.com/repos/$releaseRepo/releases?per_page=30'));
    if (list is! List) return null;
    Map? best;
    String? bestVersion;
    for (final r in list) {
      if (r is! Map || r['draft'] == true) continue;
      final tag = r['tag_name'];
      if (tag is! String || !tag.startsWith(releaseTagPrefix)) continue;
      final v = tag.substring(releaseTagPrefix.length);
      if (bestVersion == null || compareVersions(v, bestVersion) > 0) {
        best = r;
        bestVersion = v;
      }
    }
    if (best == null || bestVersion == null || compareVersions(bestVersion, current) <= 0) return null;
    final want = assetNameFor(platform, bestVersion, linuxPackage: linuxPackage);
    Uri? url, sums;
    for (final a in (best['assets'] as List? ?? const [])) {
      if (a is! Map) continue;
      if (a['name'] == want) url = Uri.parse(a['browser_download_url'] as String);
      if (a['name'] == 'SHA256SUMS') sums = Uri.parse(a['browser_download_url'] as String);
    }
    if (url == null) return null;
    String? sha;
    if (sums != null) {
      for (final line in const LineSplitter().convert(await _getText(c, sums))) {
        final parts = line.trim().split(RegExp(r'\s+\*?'));
        if (parts.length == 2 && parts[1] == want) sha = parts[0].toLowerCase();
      }
    }
    return UpdateInfo(bestVersion, (best['body'] as String?) ?? '', want, url, sha, (best['html_url'] as String?) ?? '');
  } finally {
    c.close(force: true);
  }
}

/// Downloads the update into [dir] and checks its SHA-256. Returns the file.
Future<File> downloadUpdate(UpdateInfo u, String dir, {void Function(double)? progress}) async {
  final c = HttpClient()..connectionTimeout = const Duration(seconds: 20);
  final f = File('$dir${Platform.pathSeparator}${u.assetName}');
  try {
    final req = await c.getUrl(u.assetUrl);
    req.headers.set('User-Agent', 'monitor-lite');
    final res = await req.close();
    if (res.statusCode != 200) throw HttpException('Download answered ${res.statusCode}');
    final total = res.contentLength;
    final sink = f.openWrite();
    final hashSink = _DigestSink();
    final hasher = sha256.startChunkedConversion(hashSink);
    var got = 0;
    await for (final chunk in res) {
      sink.add(chunk);
      hasher.add(chunk);
      got += chunk.length;
      if (total > 0) progress?.call(got / total);
    }
    await sink.close();
    hasher.close();
    final digest = hashSink.value.toString();
    if (u.sha256 != null && digest != u.sha256) {
      await f.delete();
      throw StateError('The download did not match the published checksum, so it was deleted.');
    }
    return f;
  } finally {
    c.close(force: true);
  }
}

class _DigestSink implements Sink<Digest> {
  late Digest value;
  @override
  void add(Digest data) => value = data;
  @override
  void close() {}
}

/// Automatic updates (settings.json `"update": {"auto": true}`, the default).
/// Collectors that can install on their own (the Windows agent, Android's
/// collector) check every [autoUpdateEvery] and install a newer release
/// without a tap. `"auto": false` turns it off for every device; the
/// "Update now" control still works.
const autoUpdateEvery = Duration(hours: 6);

/// The first check waits this long after the collector starts, so a sign-in
/// or reboot is not slowed down and a crash loop cannot hammer GitHub.
const autoUpdateFirstDelay = Duration(minutes: 10);

bool autoUpdateOn(String folder) {
  try {
    final j = jsonDecode(File('$folder${Platform.pathSeparator}settings.json').readAsStringSync());
    final u = j is Map ? j['update'] : null;
    if (u is Map && u['auto'] == false) return false;
  } catch (_) {}
  return true;
}
