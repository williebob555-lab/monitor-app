namespace MonitorApp.Client.Ui.Views;

/// <summary>
/// The whole client UI for any head. Kept as the entry point the Android head already references; it is the
/// <see cref="WorkspaceShell"/>, which holds all the layout.
/// </summary>
public sealed class MainView : WorkspaceShell
{
    /// <summary>Widths below this use the phone layout.</summary>
    public const double CompactBelowWidth = Core.ViewModels.Shell.ShellLayout.NarrowBelowWidth;
}
