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
        : base(connection, "Pair a desktop")
    {
    }

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
}
