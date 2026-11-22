using Avalonia;
using Avalonia.Controls;

namespace MonitorApp.Client.Ui.Controls;

/// <summary>Lays each child out centred on a 0..1 fraction of the panel's width (timeline event markers).</summary>
public sealed class FractionPanel : Panel
{
    public static readonly AttachedProperty<double> FractionProperty =
        AvaloniaProperty.RegisterAttached<FractionPanel, Control, double>("Fraction");

    static FractionPanel()
    {
        AffectsParentArrange<FractionPanel>(FractionProperty);
    }

    public static double GetFraction(Control control) => control.GetValue(FractionProperty);

    public static void SetFraction(Control control, double value) => control.SetValue(FractionProperty, value);

    protected override Size MeasureOverride(Size availableSize)
    {
        var height = 0.0;
        foreach (var child in Children)
        {
            child.Measure(availableSize);
            height = Math.Max(height, child.DesiredSize.Height);
        }

        return new Size(0, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        foreach (var child in Children)
        {
            var width = child.DesiredSize.Width;
            var x = Math.Clamp(GetFraction(child) * finalSize.Width - (width / 2), 0, Math.Max(0, finalSize.Width - width));
            child.Arrange(new Rect(x, 0, width, finalSize.Height));
        }

        return finalSize;
    }
}
