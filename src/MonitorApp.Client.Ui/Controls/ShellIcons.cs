using Avalonia;
using Avalonia.Controls;
using Avalonia.Data.Converters;
using Avalonia.Media;
using MonitorApp.Client.Core.Scene;

namespace MonitorApp.Client.Ui.Controls;

/// <summary>
/// Vector icons (Theme/Icons.axaml, 24 x 24 grid, stroked) and health markers. No icon font: nothing to ship, nothing to fail to load.
/// Health is always drawn as a distinct shape (circle, ring, diamond, triangle, square) next to its words, so severity
/// never depends on colour alone.
/// </summary>
public static class ShellIcons
{
    private static readonly Geometry Circle = Geometry.Parse("M6,1 A5,5 0 1 1 5.99,1 Z");
    private static readonly Geometry Ring = Geometry.Parse("F0 M6,1 A5,5 0 1 1 5.99,1 Z M6,3.5 A2.5,2.5 0 1 0 6.01,3.5 Z");
    private static readonly Geometry Diamond = Geometry.Parse("M6,0.5 L11.5,6 L6,11.5 L0.5,6 Z");
    private static readonly Geometry Triangle = Geometry.Parse("M6,1 L11.5,11 L0.5,11 Z");
    private static readonly Geometry Square = Geometry.Parse("M1,1 H11 V11 H1 Z");

    /// <summary>Maps a mode's icon key to its drawing.</summary>
    public static Geometry? Get(string? key) =>
        key is not null && Application.Current is { } app && app.TryFindResource("Icon." + key, out var value) ? value as Geometry : null;

    public static IValueConverter Icon { get; } =
        new FuncValueConverter<string?, Geometry?>(Get);

    /// <summary>Healthy: circle. Unknown: ring. Stale: diamond. Degraded: triangle. Offline: square.</summary>
    public static IValueConverter HealthShape { get; } =
        new FuncValueConverter<SceneHealth, Geometry?>(ShapeFor);

    public static IValueConverter HealthBrush { get; } =
        new FuncValueConverter<SceneHealth, IBrush?>(BrushFor);

    /// <summary>Toolbar switches: the selection tint while on, transparent while off.</summary>
    public static IValueConverter ActiveBackground { get; } =
        new FuncValueConverter<bool, IBrush?>(on => on ? Brush("Brush.Selection") : Brushes.Transparent);

    /// <summary>A filled circle: the marker for active transfer, drawn in the Transfer colour.</summary>
    public static Geometry TransferShape => Circle;

    public static Geometry ShapeFor(SceneHealth health) => health switch
    {
        SceneHealth.Healthy => Circle,
        SceneHealth.Stale => Diamond,
        SceneHealth.Degraded => Triangle,
        SceneHealth.Offline => Square,
        _ => Ring,
    };

    public static IBrush? BrushFor(SceneHealth health) => Brush(health switch
    {
        SceneHealth.Healthy => "Brush.Healthy",
        SceneHealth.Stale => "Brush.Warning",
        SceneHealth.Degraded => "Brush.Warning",
        SceneHealth.Offline => "Brush.Fault",
        _ => "Brush.Muted",
    });

    /// <summary>Binds a brush key ("Brush.Healthy") from a view model to the theme brush.</summary>
    public static IValueConverter BrushByKey { get; } =
        new FuncValueConverter<string?, IBrush?>(key => key is null ? null : Brush(key));

    /// <summary>A semantic brush from Theme/Tokens.axaml by key ("Brush.Fault").</summary>
    public static IBrush? Brush(string key) =>
        Application.Current is { } app && app.TryFindResource(key, out var value) ? value as IBrush : null;
}
