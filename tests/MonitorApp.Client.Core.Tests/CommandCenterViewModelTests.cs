using MonitorApp.Client.Core;
using MonitorApp.Client.Core.ViewModels;
using Xunit;

namespace MonitorApp.Client.Core.Tests;

public sealed class CommandCenterViewModelTests
{
    private static readonly DateTimeOffset Now = NodeFixture.Now;

    private static HistoryPointDto Point(string origin, string signal, double? number, string unit, double ageSeconds, bool? flag = null) =>
        new(origin, "stream", 1, "subject", signal, Now, Now, "test", "Origin", "Valid", "Fresh", ageSeconds,
            flag is null ? new ObservationValueDto("number", number, null, null) : new ObservationValueDto("boolean", null, null, flag),
            unit, null);

    private static TrustedPeerDto Peer(string id, string state = "Active") =>
        new(id, id == "peer-desk" ? "Desk PC" : id, "AB", state, Now, state == "Revoked" ? Now : null);

    [Fact]
    public void DevicesListSelfAndActivePeersWithOnlineStateAndHeadlineMetrics()
    {
        var points = new[]
        {
            Point("peer-self", "android.battery.level", 0.8, "ratio", 5),
            Point("peer-self", "android.battery.charging", null, "", 5, flag: true),
            Point("peer-desk", "system.cpu.utilization", 0.25, "ratio", 600),
            Point("peer-desk", "system.memory.used", 4, "bytes", 600),
            Point("peer-desk", "system.memory.total", 8, "bytes", 600),
        };

        var devices = DevicesViewModel.Build(
            "peer-self", [Peer("peer-desk"), Peer("peer-gone", "Revoked"), Peer("peer-quiet")], points);

        Assert.Equal(["peer-self", "peer-desk", "peer-quiet"], devices.Select(device => device.PeerId));
        var self = devices[0];
        Assert.Equal("This device", self.Name);
        Assert.True(self.IsOnline);
        Assert.Equal("Online", self.Status);
        Assert.Contains(self.Headline, metric => metric is { Label: "Battery", Value: "80%" });
        Assert.Contains(self.Headline, metric => metric is { Label: "Charging", Value: "yes" });
        var desktop = devices[1];
        Assert.Equal("Desk PC", desktop.Name);
        Assert.False(desktop.IsOnline);
        Assert.Equal("Offline", desktop.Status);
        Assert.Contains(desktop.Headline, metric => metric is { Label: "CPU", Value: "25%" });
        Assert.Contains(desktop.Headline, metric => metric is { Label: "Memory", Value: "50%" });
        var quiet = devices[2];
        Assert.Equal("No data yet", quiet.Status);
        Assert.False(quiet.HasHeadline);
    }

    [Fact]
    public async Task DevicesAndAlertsSectionsWorkOverTheHttpAdapterAndDefaultToTheEcosystem()
    {
        await using var node = await NodeFixture.CreateAsync();
        await node.AddNumberAsync(Now.AddMinutes(-1), 0.5);
        using var main = new MainViewModel(node.Address);

        Assert.Same(main.Environment, main.SelectedSection);
        Assert.DoesNotContain(main.Sections, section => section is PairDeviceViewModel);
        Assert.Equal(4, main.PrimarySections.Count);

        await main.Devices.RefreshAsync();
        Assert.Null(main.Devices.ErrorMessage);
        Assert.Contains(main.Devices.Devices, device => device.PeerId == "peer-local");

        var alerts = main.Sections.OfType<AlertsViewModel>().Single();
        await alerts.RefreshAsync();
        Assert.Null(alerts.ErrorMessage);
        Assert.Empty(alerts.Alerts);
        Assert.Equal("No alerts.", alerts.Summary);
        Assert.True(alerts.IsHome && alerts.Home.IsAllClear && alerts.Home.Headline == "All clear" && alerts.Home.Groups.Count == 0,
            $"empty alerts home: '{alerts.Home.Headline}', {alerts.Home.Groups.Count} groups");
    }

    [Fact]
    public async Task AlertsSectionExplainsWhenTheSourceHasNoAlerts()
    {
        await using var node = await NodeFixture.CreateAsync();
        using var connection = new NodeConnection(new NoAlertsSource(node.NewClient()));
        var alerts = new AlertsViewModel(connection);

        await alerts.RefreshAsync();

        Assert.Null(alerts.ErrorMessage);
        Assert.Equal("This node does not provide alerts.", alerts.Summary);
    }

    /// <summary>Wraps a real client but hides the optional alert capability.</summary>
    private sealed class NoAlertsSource(LocalApiClient inner) : IPeerDataSource, IDisposable
    {
        public string Description => inner.Description;

        public bool SupportsLiveUpdates => false;

        public event EventHandler? StateChanged
        {
            add { }
            remove { }
        }

        public Task<RuntimeDto> GetRuntimeAsync(CancellationToken ct = default) => inner.GetRuntimeAsync(ct);

        public Task<IReadOnlyList<TrustedPeerDto>> GetPeersAsync(CancellationToken ct = default) => inner.GetPeersAsync(ct);

        public Task<IReadOnlyList<PendingPairingDto>> GetPairingsAsync(CancellationToken ct = default) => inner.GetPairingsAsync(ct);

        public Task<CatalogDto> GetCatalogAsync(CancellationToken ct = default) => inner.GetCatalogAsync(ct);

        public Task<StateDto> GetStateAsync(
            DateTimeOffset? asOf = null, int? lookbackSeconds = null, string? subjectEntityId = null,
            string? signalId = null, CancellationToken ct = default) =>
            inner.GetStateAsync(asOf, lookbackSeconds, subjectEntityId, signalId, ct);

        public Task<HistoryResult> GetHistoryAsync(
            HistoryQuery query, int maxPages = LocalApiClient.DefaultMaxPages, int pageSize = LocalApiClient.PageSize,
            CancellationToken ct = default) => inner.GetHistoryAsync(query, maxPages, pageSize, ct);

        public Task ApprovePairingAsync(string pairingId, CancellationToken ct = default) => inner.ApprovePairingAsync(pairingId, ct);

        public Task RejectPairingAsync(string pairingId, CancellationToken ct = default) => inner.RejectPairingAsync(pairingId, ct);

        public Task RevokePeerAsync(string peerId, CancellationToken ct = default) => inner.RevokePeerAsync(peerId, ct);

        public void Dispose() => inner.Dispose();
    }
}
