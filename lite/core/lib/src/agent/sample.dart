import '../report.dart';

/// One reading of the local machine, before peers are added.
class Sample {
  Battery? battery;
  double? cpuPct;
  String? cpuNote;
  int? memUsed, memTotal, diskUsed, diskTotal;
  double? netRxBps, netTxBps;
  String? netType;
  DateTime? bootTime;
}

/// What a platform collector must provide. Counters are cumulative; the
/// collector turns them into rates by remembering the previous call.
abstract class Host {
  String get kind;
  String osName();
  String? machineKey();
  Sample sample();

  /// This process's total CPU time so far, in milliseconds.
  int? processCpuMs();

  /// ICMP echo over Tailscale. Null when it got no answer.
  Future<int?> ping(String ip);

  /// Why the last ping could not run at all (not "no answer"), for the log.
  String? get pingProblem => null;
}
