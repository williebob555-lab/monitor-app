using Avalonia.Data.Converters;
using Avalonia.Media;
using MonitorApp.Client.Core.Scene;
using MonitorApp.Client.Core.ViewModels.Dive;

namespace MonitorApp.Client.Ui.Controls;

/// <summary>Brushes and shapes for the Dive: a badge's health (or transfer) and a gauge's fill level, always from the semantic tokens.</summary>
public static class DiveConverters
{
    public static IValueConverter BadgeBrush { get; } =
        new FuncValueConverter<DiveBadge?, IBrush?>(badge => badge switch
        {
            null => ShellIcons.Brush("Brush.Muted"),
            { IsTransfer: true } => ShellIcons.Brush("Brush.Transfer"),
            { IsFault: true } => ShellIcons.Brush("Brush.Fault"),
            _ => ShellIcons.BrushFor(badge.Health),
        });

    public static IValueConverter BadgeShape { get; } =
        new FuncValueConverter<DiveBadge?, Geometry?>(badge => badge switch
        {
            null => null,
            { IsTransfer: true } => ShellIcons.TransferShape,
            { IsFault: true } => ShellIcons.ShapeFor(SceneHealth.Degraded),
            _ => ShellIcons.ShapeFor(badge.Health),
        });

    /// <summary>Normal: Healthy. High: Warning. Critical: Fault. No proportion: Transfer (a plain value, not a level).</summary>
    public static IValueConverter LevelBrush { get; } =
        new FuncValueConverter<DiveLevel, IBrush?>(level => ShellIcons.Brush(level switch
        {
            DiveLevel.Normal => "Brush.Healthy",
            DiveLevel.High => "Brush.Warning",
            DiveLevel.Critical => "Brush.Fault",
            _ => "Brush.Transfer",
        }));

    /// <summary>Large headline text colour: offline is muted (the value is gone), stale is amber (the value is old).</summary>
    public static IValueConverter HeadlineBrush { get; } =
        new FuncValueConverter<SceneHealth, IBrush?>(health => ShellIcons.Brush(health switch
        {
            SceneHealth.Stale => "Brush.Warning",
            SceneHealth.Offline => "Brush.TextMuted",
            _ => "Brush.Text",
        }));

    /// <summary>Problem rows: over-full parts in the Fault colour, everything else in its health colour (as everywhere else).</summary>
    public static IValueConverter ProblemBrush { get; } =
        new FuncValueConverter<DiveProblem?, IBrush?>(problem => problem switch
        {
            null => null,
            { LayerId: not null } => ShellIcons.Brush("Brush.Fault"),
            _ => ShellIcons.BrushFor(problem.Health),
        });

    public static IValueConverter ProblemShape { get; } =
        new FuncValueConverter<DiveProblem?, Geometry?>(problem => problem is null ? null : ShellIcons.ShapeFor(problem.Health));

    public static IValueConverter HealthBrush => ShellIcons.HealthBrush;
}
