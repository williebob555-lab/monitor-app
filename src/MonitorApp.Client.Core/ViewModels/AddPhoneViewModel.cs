using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MonitorApp.Client.Core.ViewModels;

/// <summary>
/// "Add a phone" on a computer: the "Let my phone reach this computer" switch and the pairing QR drawn in the app. Showing a
/// code only lets a phone find and verify this computer; the phone still has to be approved under Pairings.
/// </summary>
public sealed partial class AddPhoneViewModel : ObservableObject
{
    private readonly Func<IPhonePairingHost?> resolve;
    private readonly TimeProvider time;

    [ObservableProperty]
    private PeerReachDto? reach;

    [ObservableProperty]
    private PhoneInvitationDto? invitation;

    [ObservableProperty]
    private string? message;

    [ObservableProperty]
    private string? error;

    [ObservableProperty]
    private bool isBusy;

    /// <param name="resolve">The current source, read at each use: reconnecting replaces it.</param>
    public AddPhoneViewModel(Func<IPhonePairingHost?> resolve, TimeProvider? time = null)
    {
        this.resolve = resolve;
        this.time = time ?? TimeProvider.System;
    }

    /// <summary>Raised after a new code is shown, so the page can watch for the phone's pairing request.</summary>
    public event EventHandler? CodeShown;

    public bool IsReachable => Reach is { ReachableByPhone: true };

    public string ReachText => Reach?.Explanation ?? "Checking...";

    public bool CanTurnOn => Reach is { ReachableByPhone: false, CanChange: true, TailscaleAddress: not null, RestartPending: false } && !IsBusy;

    public bool CanTurnOff => Reach is { ReachableByPhone: true, CanChange: true, RestartPending: false } && !IsBusy;

    public bool CanShowCode => IsReachable && !IsBusy;

    public bool HasCode => Invitation is not null;

    public IReadOnlyList<string> QrRows => Invitation?.QrRows ?? [];

    public string CodeText => Invitation is { } code
        ? $"Can't scan? On the phone choose \"Type the code\": address {code.Address}:{code.Port}, code {code.ShortCode}. Works once, until {code.ExpiresAt.ToLocalTime():t}."
        : string.Empty;

    private IPhonePairingHost Host => resolve() ?? throw new IncompatibleApiException("Not connected to this computer's Monitor App.");

    partial void OnReachChanged(PeerReachDto? value) => NotifyAll();

    partial void OnInvitationChanged(PhoneInvitationDto? value) => NotifyAll();

    partial void OnIsBusyChanged(bool value) => NotifyAll();

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        try
        {
            Reach = await Host.GetPeerReachAsync(cancellationToken);
            var pairing = await Host.GetPhonePairingAsync(cancellationToken);
            Invitation = pairing.Invitation is { } code && code.ExpiresAt > time.GetUtcNow() ? code : null;
            Error = null;
        }
        catch (LocalApiException exception)
        {
            Error = exception.Message;
        }
    }

    [RelayCommand]
    private Task TurnOnAsync() => SetReachAsync(true);

    [RelayCommand]
    private Task TurnOffAsync() => SetReachAsync(false);

    [RelayCommand]
    private async Task ShowCodeAsync()
    {
        IsBusy = true;
        Error = null;
        try
        {
            var pairing = await Host.CreatePhoneInvitationAsync();
            Invitation = pairing.Invitation;
            Message = "Scan this with Monitor App on the phone (Pair with a desktop, then Scan QR). Then approve the phone below.";
            CodeShown?.Invoke(this, EventArgs.Empty);
        }
        catch (LocalApiException exception)
        {
            Error = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private async Task SetReachAsync(bool reachable)
    {
        IsBusy = true;
        Error = null;
        Message = reachable ? "Turning on... Monitor App restarts to apply this." : "Turning off... Monitor App restarts to apply this.";
        try
        {
            Reach = await Host.SetPeerReachAsync(reachable);
            Invitation = null;
            // The node restarts to move its listener; wait for it to come back, then show the new state.
            for (var attempt = 0; attempt < 20; attempt++)
            {
                await Task.Delay(TimeSpan.FromSeconds(3), time);
                try
                {
                    var now = await Host.GetPeerReachAsync();
                    if (!now.RestartPending)
                    {
                        Reach = now;
                        Message = now.ReachableByPhone == reachable
                            ? (reachable ? "Done. Your phone can now reach this computer over Tailscale. Press Show pairing code." : "Done. Only this computer can connect now.")
                            : now.Explanation;
                        return;
                    }
                }
                catch (LocalApiException)
                {
                    // Still restarting.
                }
                catch (HttpRequestException)
                {
                    // Still restarting.
                }
            }

            Message = "Monitor App has not come back yet. Give it a minute, then reopen this page.";
        }
        catch (LocalApiException exception)
        {
            Message = null;
            Error = exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    private void NotifyAll()
    {
        OnPropertyChanged(nameof(IsReachable));
        OnPropertyChanged(nameof(ReachText));
        OnPropertyChanged(nameof(CanTurnOn));
        OnPropertyChanged(nameof(CanTurnOff));
        OnPropertyChanged(nameof(CanShowCode));
        OnPropertyChanged(nameof(HasCode));
        OnPropertyChanged(nameof(QrRows));
        OnPropertyChanged(nameof(CodeText));
    }
}
