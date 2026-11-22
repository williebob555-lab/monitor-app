using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MonitorApp.Client.Core.ViewModels;

public sealed partial class OverviewViewModel : SectionViewModel
{
    [ObservableProperty]
    private string runtimeStatus = "Not loaded";

    [ObservableProperty]
    private string scope = string.Empty;

    [ObservableProperty]
    private string localPeerId = "Not initialized";

    [ObservableProperty]
    private string localFingerprint = string.Empty;

    [ObservableProperty]
    private int pendingPairingCount;

    public OverviewViewModel(NodeConnection connection)
        : base(connection, "Overview")
    {
    }

    public ObservableCollection<TrustedPeerRow> Peers { get; } = [];

    /// <summary>True when the host can start and stop background collection (phone foreground service).</summary>
    public bool HasHostControl => Client is IHostServiceControl;

    [RelayCommand]
    private async Task StartHostAsync()
    {
        if (Client is IHostServiceControl host)
        {
            host.RequestStart();
            StatusMessage = "Starting monitoring.";
            await Task.Delay(1500);
            await RefreshAsync();
        }
    }

    [RelayCommand]
    private async Task StopHostAsync()
    {
        if (Client is IHostServiceControl host)
        {
            host.RequestStop();
            StatusMessage = "Stopping monitoring.";
            await Task.Delay(1500);
            await RefreshAsync();
        }
    }

    [RelayCommand]
    public override async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await RunAsync(async () =>
        {
            var runtime = await Client.GetRuntimeAsync(cancellationToken);
            var peers = await Client.GetPeersAsync(cancellationToken);
            RuntimeStatus = runtime.Status;
            Scope = runtime.Scope;
            LocalPeerId = runtime.LocalPeer?.PeerId ?? "Not initialized";
            LocalFingerprint = runtime.LocalPeer?.FingerprintSha256 ?? string.Empty;
            PendingPairingCount = runtime.PendingPairingCount;
            Peers.Clear();
            foreach (var peer in peers)
            {
                Peers.Add(TrustedPeerRow.From(peer));
            }
        });
    }
}
