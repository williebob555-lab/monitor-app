using System.Text.Json;
using MonitorApp.Agent.Core;
using MonitorApp.Contracts.V1;
using MonitorApp.Peer.Application;
using MonitorApp.Peer.Collection;
using MonitorApp.Peer.Collection.Discovery;
using MonitorApp.Peer.Collection.Hardware;
using MonitorApp.Peer.Collection.Lan;
using MonitorApp.Peer.Collection.Services;
using MonitorApp.Peer.LocalApi;
using MonitorApp.Peer.Persistence;

namespace MonitorApp.Peer.Node;

/// <summary>
/// What a computer finds out by itself (bill of work D1-D5): its hardware, its services (Tailscale, Syncthing and others),
/// the other devices on its tailnet, and, only while the "Find devices on this network" switch is on, the devices on its
/// own home network. Everything becomes signed readings that replicate to the mesh like system readings.
/// </summary>
public sealed class NodeSelfDiscovery : INetworkDiscoveryController, IAsyncDisposable
{
    public const string SettingFileName = "discovery.json";
    public static readonly TimeSpan DiscoveryInterval = TimeSpan.FromMinutes(5);
    public static readonly TimeSpan ServicesInterval = TimeSpan.FromMinutes(1);
    public static readonly TimeSpan HardwareInterval = TimeSpan.FromHours(6);

    private readonly string settingPath;
    private readonly string selfSubject;
    private readonly NetworkDiscovery discovery;
    private readonly SubjectObservationsWriter writer;
    private readonly TailscaleStatusReader tailscale;
    private readonly NodePeriodicTask discoveryTask;
    private readonly NodePeriodicTask servicesTask;
    private readonly NodePeriodicTask hardwareTask;
    private readonly TimeProvider time;
    private readonly object sync = new();
    private bool isEnabled;
    private int running;

    public NodeSelfDiscovery(string dataDirectory, PeerStore store, ILocalPeerIdentityProvider identity, ISelfInterfaceCollector interfaces, TimeProvider? time = null)
    {
        this.time = time ?? TimeProvider.System;
        settingPath = Path.Combine(dataDirectory, SettingFileName);
        isEnabled = ReadEnabled(settingPath);
        var peerId = identity.PublicIdentity.PeerId;
        selfSubject = $"peer:{peerId}";
        var bootId = Guid.NewGuid().ToString("N");
        discovery = new NetworkDiscovery(interfaces, NeighborTables.Create(), new LocalNetworkProbes(), this.time);
        writer = new SubjectObservationsWriter(store, identity, NetworkDiscovery.CollectorId, bootId, () => this.time.GetUtcNow());
        var tailscaleWriter = new SubjectObservationsWriter(store, identity, "monitor.tailscale.devices", bootId, () => this.time.GetUtcNow());
        tailscale = new TailscaleStatusReader();
        var services = new PeerSystemMetricsIngestionBridge(
            store, identity,
            new ServiceSnapshotCollector(new SystemServiceProbe(), [tailscale, new SyncthingStatusReader()], this.time),
            PeerCollectionOptions.Default(selfSubject, bootId) with { CollectorId = "monitor.services" },
            () => this.time.GetUtcNow());
        var hardware = new PeerSystemMetricsIngestionBridge(
            store, identity, new HardwareSnapshotCollector(time: this.time),
            PeerCollectionOptions.Default(selfSubject, bootId) with { CollectorId = HardwareSignals.CollectorId, CollectionTimeout = TimeSpan.FromMinutes(2) },
            () => this.time.GetUtcNow());

        discoveryTask = new NodePeriodicTask("network-discovery", DiscoveryInterval, RunDiscoveryAsync, this.time);
        var lastTailscaleWrite = DateTimeOffset.MinValue;
        servicesTask = new NodePeriodicTask("services", ServicesInterval, async token =>
        {
            await services.RunOnceAsync(token).ConfigureAwait(false);
            var now = this.time.GetUtcNow();
            if (now - lastTailscaleWrite >= DiscoveryInterval && tailscale.LastDevices.Count > 0)
            {
                await tailscaleWriter.WriteAsync(now, TailscaleReadings(tailscale.LastDevices), token).ConfigureAwait(false);
                lastTailscaleWrite = now;
            }
        }, this.time);
        hardwareTask = new NodePeriodicTask("hardware", HardwareInterval, async token => await hardware.RunOnceAsync(token).ConfigureAwait(false), this.time);
    }

    public void Start()
    {
        hardwareTask.Start();
        servicesTask.Start();
        discoveryTask.Start();
    }

    public NetworkDiscoveryStatus GetStatus()
    {
        var last = discovery.Last;
        bool on;
        lock (sync) on = isEnabled;
        return new NetworkDiscoveryStatus(
            on,
            Volatile.Read(ref running) == 1,
            last?.At,
            last?.Devices.Count ?? 0,
            last?.Devices.Count(d => d.Answered) ?? 0,
            last?.Networks ?? [],
            last?.Problem,
            on
                ? "On. Every 5 minutes this computer reads its neighbor table and asks its own home network (mDNS, UPnP, local names) " +
                  "who is there; every 30 minutes it also pings its own subnet, 16 addresses at a time. Nothing outside your home network is contacted, " +
                  "nothing is logged in to, and found devices are never trusted. They appear on the Map and in Things, marked as discovered."
                : "Off. This computer does not look at other devices on your network. Turn it on to see your router, printers, TVs and other " +
                  "devices on the Map. It only looks at this computer's own home network.");
    }

    public NetworkDiscoveryStatus SetEnabled(bool enabled)
    {
        lock (sync)
        {
            isEnabled = enabled;
            var directory = Path.GetDirectoryName(settingPath);
            if (directory is not null) Directory.CreateDirectory(directory);
            File.WriteAllText(settingPath, JsonSerializer.Serialize(new { enabled }));
        }

        if (enabled)
        {
            _ = Task.Run(() => RunDiscoveryAsync(CancellationToken.None));
        }

        return GetStatus();
    }

    private async Task RunDiscoveryAsync(CancellationToken cancellationToken)
    {
        lock (sync)
        {
            if (!isEnabled) return;
        }

        if (Interlocked.CompareExchange(ref running, 1, 0) != 0) return;
        try
        {
            var pass = await discovery.RunAsync(cancellationToken).ConfigureAwait(false);
            await writer.WriteAsync(pass.At.ToUniversalTime(), NetworkDiscovery.Readings(selfSubject, pass), cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is IOException or InvalidOperationException or System.Net.Sockets.SocketException or UnauthorizedAccessException)
        {
            // The next pass tries again; the status keeps the last good pass.
        }
        finally
        {
            Volatile.Write(ref running, 0);
        }
    }

    /// <summary>Tailnet devices as their own subjects (ts:{id}), described with the same signals as discovered devices.</summary>
    public static IReadOnlyList<SubjectReadings> TailscaleReadings(IReadOnlyList<TailscaleDevice> devices) =>
        [.. devices.Select(device =>
        {
            var metrics = new List<CollectedMetric>
            {
                Text(DiscoverySignals.Name, device.Name),
                Text(DiscoverySignals.Network, "Tailscale"),
                Text(DiscoverySignals.Found, "Tailscale"),
                Text(DiscoverySignals.Kind, KindFor(device.Os), ObservationQuality.Estimated),
                new(DiscoverySignals.Answered, MetricUnit.Boolean, ObservationQuality.Valid, new MetricValue { BooleanValue = device.Online }),
            };
            if (device.Address is not null) metrics.Add(Text(DiscoverySignals.Address, device.Address));
            if (device.Os is not null) metrics.Add(Text(DiscoverySignals.Os, device.Os));
            if (device.IsExitNode) metrics.Add(Text(DiscoverySignals.Offers, "exit node"));
            return new SubjectReadings("ts:" + device.Id, metrics);
        })];

    private static string KindFor(string? os) => os?.ToLowerInvariant() switch
    {
        "ios" or "android" => "device.phone",
        "windows" or "linux" or "macos" or "freebsd" => "device.computer",
        _ => "device",
    };

    private static CollectedMetric Text(string id, string value, ObservationQuality quality = ObservationQuality.Valid) =>
        new(id, MetricUnit.Unspecified, quality, new MetricValue { StringValue = value });

    private static bool ReadEnabled(string path)
    {
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllBytes(path));
            return document.RootElement.TryGetProperty("enabled", out var value) && value.ValueKind == JsonValueKind.True;
        }
        catch (Exception exception) when (exception is IOException or JsonException or UnauthorizedAccessException)
        {
            return false; // missing or unreadable: off, as the owner decided
        }
    }

    public async ValueTask DisposeAsync()
    {
        await discoveryTask.DisposeAsync().ConfigureAwait(false);
        await servicesTask.DisposeAsync().ConfigureAwait(false);
        await hardwareTask.DisposeAsync().ConfigureAwait(false);
    }
}
