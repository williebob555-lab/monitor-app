# T300 — Cisco SG250 read-only managed-switch spike

This is an isolated collection adapter, not an automatically enabled node collector or a supported hardware claim. The Cisco SG250 / Cisco 250 Series exposes the IF-MIB, BRIDGE-MIB, and Q-BRIDGE-MIB portions used here. No switch credentials are synchronized between Monitor App peers.

## What is implemented

`CiscoSg250Adapter` converts read-only SNMP values to a timestamped `Sg250Snapshot` and an existing `LanTopologyFragment`. It reports named Ethernet switch ports only when IF-MIB explicitly identifies `ifType=ethernetCsmacd(6)` (excluding VLAN, LAG and other logical interfaces), administrative/operational state, `ifHighSpeed` in Mbps when present, and a port's PVID **only** when `dot1dBasePortIfIndex` explicitly maps a bridge port to IF-MIB's ifIndex. A zero `ifHighSpeed` is unknown, not 0 Mbps. Unknown or unavailable results are never converted to numeric zero.

The provisioned `switchEntityId` is independent of the management IP and the collecting peer. The reported `dot1dBaseBridgeAddress` is optional evidence for a stable bridge MAC identity. Port identifiers are scoped beneath that switch identity. Only `Port -> Switch: AttachedTo` relationships are emitted as device-reported evidence. The spike **does not infer** physical uplinks, LLDP neighbors, trunk configuration, VLAN membership beyond PVID, or a cable path from IP connectivity. A PVID is retained as port information, not projected into an invalid Port-to-VLAN relationship.

All observations and identity claims include the explicitly configured collecting peer and observation time. No secret or raw exception is included in snapshot error messages. The adapter returns `Unavailable`, `PermissionDenied` or `Error` separately, with no fictional topology.

## Transport and local provisioning

`NetSnmpV3Sg250Reader` uses **SNMPv3 authPriv only**, via the locally installed Net-SNMP `snmpbulkwalk` and `snmpget` executables. It performs read requests only, using private IPv4 management addresses. Executable paths and `SNMPCONFPATH` must be absolute. Protocol arguments are constructed without a shell; authentication/privacy passphrases **must not** be provided as command-line flags or stored in repository configuration.

The collector administrator must provision a read-only SNMPv3 account on the switch and a tightly permission-restricted, local Net-SNMP `snmp.conf` directory for the service identity. That local file may contain the Net-SNMP `defAuthPassphrase` and `defPrivPassphrase` values; keep it readable only by the service account, and outside shared or replicated paths. Confirm the selected Net-SNMP build honors `SNMPCONFPATH` and the configured authentication/privacy algorithms. On Linux, protect the directory with mode 0700 and configuration file with mode 0600. On Windows, restrict the corresponding ACL to the service identity and administrators. Do not place credentials in an environment variable, command argument or log.

The following is an **opt-in local composition example**, not a deployed node registration:

```csharp
var source = new NetSnmpV3Sg250Reader(
    IPAddress.Parse("192.168.1.20"), "monitor-reader",
    "/var/lib/monitor-app/snmp", "/usr/bin/snmpbulkwalk", "/usr/bin/snmpget");
var adapter = new CiscoSg250Adapter("switch:rack-sg250", collectorPeerId, source);
Sg250Snapshot snapshot = await adapter.CollectAsync(cancellationToken);
```

Collection is explicitly assigned to one peer; a second peer must be separately provisioned before takeover. Avoid polling a switch from multiple peers by default. No new listener, management API endpoint, credentials table, automatic failover or scheduler is introduced by T300.

## Verification / next boundaries

Unit tests in `tests/MonitorApp.Peer.Collection.Tests/CiscoSg250Tests.cs` exercise production parsing/projection, bridge-port indexing, strong-vs-weak identity, failed reads, unknown values, and private-target enforcement.

Run `dotnet test tests/MonitorApp.Peer.Collection.Tests/MonitorApp.Peer.Collection.Tests.csproj -c Release` and `dotnet format MonitorApp.sln --verify-no-changes` from the repository root. Those commands require the pinned .NET 10 SDK. A real SG250 probe, Windows/Linux execution, firmware differences, and integration with node scheduling/SQLite persistence must be verified separately. Until that integration is ready, T300 remains an **unwired adapter spike** and should not be presented as a working switch-monitoring UI or automated collector.

Sources for protocol support: Cisco 250 Series Smart Switches data sheet and Cisco 250 Series administration guide. Firmware versions may differ.
