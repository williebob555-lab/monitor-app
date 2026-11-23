using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MonitorApp.Client.Core.ViewModels;

/// <summary>Pending pairing decisions and trust revocation. Revocation needs an explicit second step.</summary>
public sealed partial class PairingsViewModel : SectionViewModel
{
    /// <summary>"Add a phone" (reach switch and in-app QR); null when the source is not a computer's own node.</summary>
    public AddPhoneViewModel? AddPhone { get; }

    public bool CanAddPhone => AddPhone is not null;

    [ObservableProperty]
    private TrustedPeerRow? pendingRevocation;

    public PairingsViewModel(NodeConnection connection)
        : base(connection, "Pairing and trust")
    {
        if (Client is IPhonePairingHost)
        {
            AddPhone = new AddPhoneViewModel(() => Client as IPhonePairingHost);
            AddPhone.CodeShown += (_, _) => _ = WatchForPhoneAsync();
        }
    }

    /// <summary>After a code is shown, look for the phone's request every few seconds for ten minutes so it appears without a refresh.</summary>
    private async Task WatchForPhoneAsync()
    {
        var before = Pending.Count;
        for (var attempt = 0; attempt < 200 && Pending.Count <= before; attempt++)
        {
            await Task.Delay(TimeSpan.FromSeconds(3));
            try
            {
                await ReloadAsync(CancellationToken.None);
            }
            catch (LocalApiException)
            {
                return;
            }
        }
    }

    public ObservableCollection<PendingPairingRow> Pending { get; } = [];

    public ObservableCollection<TrustedPeerRow> Peers { get; } = [];

    /// <summary>Per-phone permission panels; empty and hidden when the source cannot manage grants (the in-process phone runtime).</summary>
    public ObservableCollection<PeerGrantViewModel> Grants { get; } = [];

    public bool CanGrant => Client is ISecuritySource;

    public bool IsRevocationPending => PendingRevocation is not null;

    public string RevocationPrompt => PendingRevocation is null
        ? string.Empty
        : $"Remove {PendingRevocation.Name} (fingerprint {PendingRevocation.Fingerprint}) from all your devices? " +
            "Every device stops trusting it, and it cannot be added back without reinstalling it.";

    partial void OnPendingRevocationChanged(TrustedPeerRow? value)
    {
        OnPropertyChanged(nameof(IsRevocationPending));
        OnPropertyChanged(nameof(RevocationPrompt));
    }

    [RelayCommand]
    public override async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        await RunAsync(() => ReloadAsync(cancellationToken));
        if (AddPhone is not null)
        {
            await AddPhone.RefreshAsync(cancellationToken);
        }
    }

    [RelayCommand]
    private async Task ApproveAsync(PendingPairingRow? pairing)
    {
        if (pairing is not { CanDecide: true })
        {
            return;
        }

        await Decide(pairing, approve: true);
    }

    [RelayCommand]
    private async Task RejectAsync(PendingPairingRow? pairing)
    {
        if (pairing is not { CanDecide: true })
        {
            return;
        }

        await Decide(pairing, approve: false);
    }

    [RelayCommand]
    private void RequestRevoke(TrustedPeerRow? peer)
    {
        if (peer is { CanRevoke: true })
        {
            PendingRevocation = peer;
            StatusMessage = null;
        }
    }

    [RelayCommand]
    private void CancelRevoke() => PendingRevocation = null;

    [RelayCommand]
    private async Task ConfirmRevokeAsync()
    {
        var peer = PendingRevocation;
        if (peer is null)
        {
            return;
        }

        PendingRevocation = null;
        await RunAsync(async () =>
        {
            await Client.RevokePeerAsync(peer.PeerId);
            StatusMessage = $"Removed {peer.Name} from all your devices.";
            await ReloadAsync(CancellationToken.None);
        });
    }

    private async Task Decide(PendingPairingRow pairing, bool approve)
    {
        await RunAsync(async () =>
        {
            if (approve)
            {
                await Client.ApprovePairingAsync(pairing.PairingId);
                StatusMessage = $"Approved pairing with {pairing.Name} (fingerprint {pairing.Fingerprint}). " +
                    "The peer becomes trusted when it completes pairing.";
            }
            else
            {
                await Client.RejectPairingAsync(pairing.PairingId);
                StatusMessage = $"Rejected pairing with {pairing.Name}.";
            }

            await ReloadAsync(CancellationToken.None);
        });
    }

    private async Task ReloadAsync(CancellationToken cancellationToken)
    {
        var pending = await Client.GetPairingsAsync(cancellationToken);
        var peers = await Client.GetPeersAsync(cancellationToken);
        var stored = Client is ISecuritySource security
            ? (await security.GetControlGrantsAsync(cancellationToken)).ToDictionary(g => g.PeerId)
            : [];
        Pending.Clear();
        foreach (var pairing in pending)
        {
            Pending.Add(PendingPairingRow.From(pairing));
        }

        Peers.Clear();
        var previous = Grants.ToDictionary(g => g.PeerId);
        Grants.Clear();
        foreach (var peer in peers)
        {
            var row = TrustedPeerRow.From(peer);
            Peers.Add(row);
            if (CanGrant && row.CanRevoke)
            {
                // Keep the person's unsaved toggles across a refresh; otherwise show what is stored.
                if (!previous.TryGetValue(row.PeerId, out var model))
                {
                    model = new PeerGrantViewModel(row.PeerId, row.Name, () => Client as ISecuritySource);
                }

                if (!model.IsDirty)
                {
                    model.Load(stored.GetValueOrDefault(row.PeerId));
                }

                Grants.Add(model);
            }
        }

        OnPropertyChanged(nameof(CanGrant));
    }
}
