using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MonitorApp.Client.Core.ViewModels;

/// <summary>
/// "Find devices on this network" on a computer (owner decision 2026-11-24: a switch per computer, off until turned on). Every
/// press answers at once: the button changes to "Turning on..." and the result or the error is shown in words.
/// </summary>
public sealed partial class NetworkDiscoveryViewModel : ObservableObject
{
    private readonly Func<INetworkDiscoverySource?> resolve;

    [ObservableProperty]
    private NetworkDiscoveryDto? status;

    [ObservableProperty]
    private string? message;

    [ObservableProperty]
    private bool isBusy;

    /// <param name="resolve">The current source, read at each use: reconnecting replaces it.</param>
    public NetworkDiscoveryViewModel(Func<INetworkDiscoverySource?> resolve) => this.resolve = resolve;

    public bool IsAvailable => resolve() is not null && Status is not null;

    public bool IsOn => Status is { Enabled: true };

    public bool CanTurnOn => Status is { Enabled: false } && !IsBusy;

    public bool CanTurnOff => Status is { Enabled: true } && !IsBusy;

    public string Headline => Status switch
    {
        null => "Checking...",
        { Enabled: false } => "Off: only this computer and its paired devices are shown",
        { Running: true, LastPassAt: null } => "Looking for devices now...",
        { Problem: { } problem } => problem,
        { LastPassAt: null } => "On. The first look starts within a minute.",
        { } s => string.Create(CultureInfo.CurrentCulture,
            $"Found {s.DevicesFound} device{(s.DevicesFound == 1 ? string.Empty : "s")} on {string.Join(", ", s.Networks)}, {s.DevicesAnswered} online · last looked {s.LastPassAt!.Value.ToLocalTime():t}{(s.Running ? " · looking again now" : string.Empty)}"),
    };

    public string Explanation => Status?.Explanation ?? string.Empty;

    public async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (resolve() is not { } source)
        {
            Status = null;
            return;
        }

        try
        {
            Status = await source.GetNetworkDiscoveryAsync(cancellationToken);
        }
        catch (LocalApiException)
        {
            Status = null; // an older computer without the switch: the card stays hidden
        }
    }

    [RelayCommand]
    private Task TurnOn() => SetAsync(true);

    [RelayCommand]
    private Task TurnOff() => SetAsync(false);

    private async Task SetAsync(bool enabled)
    {
        if (resolve() is not { } source) return;
        IsBusy = true;
        Message = enabled ? "Turning on..." : "Turning off...";
        try
        {
            Status = await source.SetNetworkDiscoveryAsync(enabled);
            Message = enabled
                ? "On. Found devices appear on the Map and in Things within a minute or two."
                : "Off. Devices already found stay listed until you remove them; nothing new is looked for.";
        }
        catch (LocalApiException exception)
        {
            Message = "That didn't work: " + exception.Message;
        }
        finally
        {
            IsBusy = false;
        }
    }

    partial void OnStatusChanged(NetworkDiscoveryDto? value) => NotifyAll();

    partial void OnIsBusyChanged(bool value) => NotifyAll();

    private void NotifyAll()
    {
        OnPropertyChanged(nameof(IsAvailable));
        OnPropertyChanged(nameof(IsOn));
        OnPropertyChanged(nameof(CanTurnOn));
        OnPropertyChanged(nameof(CanTurnOff));
        OnPropertyChanged(nameof(Headline));
        OnPropertyChanged(nameof(Explanation));
    }
}
