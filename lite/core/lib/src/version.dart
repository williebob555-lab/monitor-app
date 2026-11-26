/// Set at build time with --define=APP_VERSION=x.y.z (agent) or
/// --dart-define=APP_VERSION=x.y.z (app). "dev" means a local build.
const String appVersion = String.fromEnvironment('APP_VERSION', defaultValue: 'dev');

/// Compares two dotted versions. "dev" sorts below everything.
int compareVersions(String a, String b) {
  List<int> parts(String v) {
    if (v == 'dev') return const [-1];
    return v.split(RegExp(r'[.+-]')).take(3).map((p) => int.tryParse(p) ?? 0).toList();
  }

  final pa = parts(a), pb = parts(b);
  for (var i = 0; i < 3; i++) {
    final x = i < pa.length ? pa[i] : 0, y = i < pb.length ? pb[i] : 0;
    if (x != y) return x.compareTo(y);
  }
  return 0;
}
