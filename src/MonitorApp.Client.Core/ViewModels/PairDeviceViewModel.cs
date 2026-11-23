using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MonitorApp.Client.Core.ViewModels;

/// <summary>
/// Pair this device with a desktop: a typed address and one-time code, or a pasted invitation. The desktop user
/// must still approve the request there. Camera QR scanning is a later addition to the same commands.
/// </summary>
public sealed partial class PairDeviceViewModel : SectionViewModel
{
    [ObservableProperty]
    private string addressText = string.Empty;

    [ObservableProperty]
    private string codeText = string.Empty;

    [ObservableProperty]
    private string invitationText = string.Empty;

    public PairDeviceViewModel(NodeConnection connection)
        : base(connection, "Pair a device")
    {
    }

    /// <summary>Raised after a pairing succeeds so the shell can refresh what depends on the trusted peers.</summary>
    public event EventHandler? Paired;

    /// <summary>True when the source can scan a pairing QR with a camera; the view shows the Scan QR button only then.</summary>
    public bool CanScanQr => Client is IQrPairingSource { CanScanQr: true };

    public override Task RefreshAsync(CancellationToken cancellationToken = default) => Task.CompletedTask;

    [RelayCommand]
    private async Task PairWithCodeAsync()
    {
        if (Client is not IPairingInitiator initiator)
        {
            ErrorMessage = "This device cannot start pairing.";
            return;
        }

        StatusMessage = "Contacting the desktop. Approve this device on the desktop when asked.";
        if (await RunAsync(async () =>
            {
                var peer = await initiator.PairWithCodeAsync(AddressText, CodeText);
                StatusMessage = $"Paired with {peer}.";
                CodeText = string.Empty;
            }))
        {
            return;
        }

        StatusMessage = null;
    }

    [RelayCommand]
    private async Task PairWithInvitationAsync()
    {
        if (Client is not IPairingInitiator initiator)
        {
            ErrorMessage = "This device cannot start pairing.";
            return;
        }

        StatusMessage = "Contacting the desktop. Approve this device on the desktop when asked.";
        if (await RunAsync(async () =>
            {
                var peer = await initiator.PairWithInvitationAsync(InvitationText);
                StatusMessage = $"Paired with {peer}.";
                InvitationText = string.Empty;
            }))
        {
            return;
        }

        StatusMessage = null;
    }

    [RelayCommand(CanExecute = nameof(CanScanQr))]
    private async Task ScanQrAsync()
    {
        if (Client is not IQrPairingSource scanner)
        {
            return;
        }

        StatusMessage = null;
        await RunAsync(async () =>
        {
            // null means the person backed out of the scanner: no message. ApiValidationException is shown by RunAsync.
            if (await scanner.ScanAndPairAsync() is { } peer)
            {
                StatusMessage = $"Paired with {peer}.";
                Paired?.Invoke(this, EventArgs.Empty);
            }
        });
    }
}
