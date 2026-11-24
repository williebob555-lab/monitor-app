using System.Globalization;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MonitorApp.Client.Core.ViewModels;

/// <summary>
/// This computer's updates in plain words: installed version, when it last looked, what it found, a "Check for updates" button and,
/// when a verified update is waiting, an Install button (fingerprint when enforced). Nothing installs without that press.
/// </summary>
public sealed partial class UpdatesViewModel : SectionViewModel
{
    private readonly TimeProvider time;

    [ObservableProperty]
    private LocalUpdateStatusDto? status;

    public UpdatesViewModel(NodeConnection connection, TimeProvider? time = null)
        : base(connection, "Updates")
    {
        this.time = time ?? TimeProvider.System;
    }

    private ILocalUpdatesSource? Source => Client as ILocalUpdatesSource;

    public string InstalledText => Status is { } s ? $"Installed version {s.InstalledVersion}" : "Checking...";

    public string LastCheckedText => Status switch
    {
        null => string.Empty,
        { LastCheckedUtc: { } at } => $"Last checked {Ago(time.GetUtcNow() - at)} ({at.ToLocalTime().ToString("g", CultureInfo.CurrentCulture)}). It checks by itself every hour.",
        _ => "Not checked yet since Monitor App started. It checks by itself every hour.",
    };

    public string StateText => Status switch
    {
        null => string.Empty,
        { Stage: "Disabled" } s => $"Updates are turned off: {s.Detail}",
        { ApprovalPending: true } s => $"Version {s.TargetVersion} is downloaded, verified and ready to install. It installs only when you press Install.",
        { Stage: "Staged" } s => $"Version {s.TargetVersion} is downloaded and verified, but this installation cannot install updates by itself. Install it from the release page.",
        { Stage: "Applying" } s => $"Installing {s.TargetVersion}... Monitor App restarts when it is done.",
        { Stage: "Failed" or "RolledBack" } s => $"Installing {s.TargetVersion} did not work, and the previous version was kept. {s.Detail}",
        _ => "You have the newest version.",
    };

    public string? CheckErrorText => Status?.LastError is { } error ? $"The last check did not finish: {error}" : null;

    public bool CanInstall => Status is { ApprovalPending: true, TargetVersion: not null };

    public string InstallLabel => Status?.TargetVersion is { } version ? $"Install {version}" : "Install";

    partial void OnStatusChanged(LocalUpdateStatusDto? value)
    {
        OnPropertyChanged(nameof(InstalledText));
        OnPropertyChanged(nameof(LastCheckedText));
        OnPropertyChanged(nameof(StateText));
        OnPropertyChanged(nameof(CheckErrorText));
        OnPropertyChanged(nameof(CanInstall));
        OnPropertyChanged(nameof(InstallLabel));
        InstallCommand.NotifyCanExecuteChanged();
    }

    public override async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (Source is not { } source)
        {
            return;
        }

        await RunAsync(async () => Status = await source.GetLocalUpdateStatusAsync(cancellationToken));
    }

    [RelayCommand]
    private async Task CheckAsync()
    {
        if (Source is not { } source)
        {
            return;
        }

        StatusMessage = "Checking for updates...";
        var before = Status?.LastCheckedUtc;
        if (await RunAsync(async () => Status = await source.CheckForUpdatesAsync()))
        {
            // Always answer the press: what the check found, or why it did not run just now.
            StatusMessage = Status switch
            {
                { LastError: { } error } => $"The check did not finish: {error}",
                { LastCheckedUtc: { } at } when at == before && before is not null =>
                    $"Checked {Ago(time.GetUtcNow() - at)}, so it did not check again yet (it waits 30 seconds between checks). {StateText}",
                _ => $"Checked just now. {StateText}",
            };
        }
        else
        {
            StatusMessage = null;
        }
    }

    [RelayCommand(CanExecute = nameof(CanInstall))]
    private async Task InstallAsync()
    {
        if (Source is not { } source || Status is not { ApprovalPending: true, TargetVersion: { } version })
        {
            return;
        }

        StatusMessage = $"Starting the install of {version}...";
        if (await RunAsync(async () =>
            {
                await source.ApproveUpdateAsync(version);
                Status = await source.GetLocalUpdateStatusAsync();
            }))
        {
            StatusMessage = $"Installing {version}. Confirm if asked; Monitor App restarts when it is done.";
        }
        else
        {
            StatusMessage = null;
        }
    }

    private static string Ago(TimeSpan elapsed) => elapsed switch
    {
        { TotalMinutes: < 1 } => "just now",
        { TotalMinutes: < 2 } => "1 minute ago",
        { TotalHours: < 1 } => $"{(int)elapsed.TotalMinutes} minutes ago",
        { TotalHours: < 2 } => "1 hour ago",
        { TotalDays: < 1 } => $"{(int)elapsed.TotalHours} hours ago",
        _ => $"{(int)elapsed.TotalDays} days ago",
    };
}
