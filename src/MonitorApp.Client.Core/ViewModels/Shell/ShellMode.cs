namespace MonitorApp.Client.Core.ViewModels.Shell;

/// <summary>
/// A workspace mode: a perspective on the same objects (UI spec section 3). Only modes that exist today are listed.
/// Physical, Inventory, Projects and Sourcing are deliberately absent until they have an implementation; to add one,
/// add a value here, one <see cref="ShellModeItem"/> in <see cref="ShellViewModel.ModesFor"/> and one content entry in
/// <see cref="ShellViewModel.ContentFor"/>.
/// </summary>
public enum ShellMode
{
    Ecosystem,
    Alerts,
    Activity,
    Search,
    Settings,
}

/// <summary>A navigation entry. <paramref name="IconKey"/> is resolved to a drawing by the view layer.</summary>
/// <param name="Title">Full name, used by the side rail and as the accessible name.</param>
/// <param name="NarrowTitle">Short label for the phone bottom bar ("History", "More").</param>
public sealed record ShellModeItem(ShellMode Mode, string Title, string NarrowTitle, string IconKey, string Tooltip);

/// <summary>Layout class chosen from the available width (UI spec section 19).</summary>
public enum ShellLayoutClass
{
    /// <summary>Rail, toolbar, workspace, docked inspector and timeline.</summary>
    Wide,

    /// <summary>Like wide, but the inspector is an overlay panel.</summary>
    Medium,

    /// <summary>Phone: bottom navigation, bottom-sheet inspector, reduced toolbar.</summary>
    Narrow,
}

public static class ShellLayout
{
    public const double WideFromWidth = 1000;
    public const double NarrowBelowWidth = 720;

    /// <summary>Zero (not measured yet) counts as wide so the first frame is not a phone layout.</summary>
    public static ShellLayoutClass Classify(double width) => width switch
    {
        <= 0 => ShellLayoutClass.Wide,
        < NarrowBelowWidth => ShellLayoutClass.Narrow,
        < WideFromWidth => ShellLayoutClass.Medium,
        _ => ShellLayoutClass.Wide,
    };
}
