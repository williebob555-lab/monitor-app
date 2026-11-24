using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace MonitorApp.Client.Core.ViewModels;

/// <summary>
/// "Diagnostic log": what this device did with pairing, connections (and why they failed), removals and updates, newest
/// at the bottom, with a Copy button so it can be pasted into a message. Wilson, 2026-11-24: "add logs ... that you can
/// read and figure this out with". Keys are cut to eight characters before they are written.
/// </summary>
public sealed partial class DiagnosticsViewModel(NodeConnection connection) : SectionViewModel(connection, "Diagnostic log")
{
    public const int Lines = 300;

    [ObservableProperty]
    private string text = string.Empty;

    [RelayCommand]
    public override async Task RefreshAsync(CancellationToken cancellationToken = default)
    {
        if (Client is not IDiagnosticsSource source)
        {
            return;
        }

        await RunAsync(async () =>
        {
            var lines = await source.GetDiagnosticLogAsync(Lines, cancellationToken);
            Text = lines.Count == 0 ? "Nothing recorded since this device started." : string.Join(Environment.NewLine, lines);
        });
    }
}
