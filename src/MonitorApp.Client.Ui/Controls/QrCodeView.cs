using Avalonia;
using Avalonia.Controls;
using Avalonia.Media;

namespace MonitorApp.Client.Ui.Controls;

/// <summary>
/// Draws a QR code from rows of '1' (dark) and '0' (light) modules, always black on white with a quiet zone, whatever the
/// theme, so phone cameras read it.
/// </summary>
public sealed class QrCodeView : Control
{
    public static readonly StyledProperty<IReadOnlyList<string>?> RowsProperty =
        AvaloniaProperty.Register<QrCodeView, IReadOnlyList<string>?>(nameof(Rows));

    static QrCodeView()
    {
        AffectsRender<QrCodeView>(RowsProperty);
    }

    public IReadOnlyList<string>? Rows
    {
        get => GetValue(RowsProperty);
        set => SetValue(RowsProperty, value);
    }

    public override void Render(DrawingContext context)
    {
        var rows = Rows;
        var side = Math.Min(Bounds.Width, Bounds.Height);
        if (rows is not { Count: > 0 } || side <= 0)
        {
            return;
        }

        const int quiet = 4;
        var modules = rows.Count + (2 * quiet);
        var cell = Math.Floor(side / modules);
        if (cell < 1)
        {
            return;
        }

        var size = cell * modules;
        context.FillRectangle(Brushes.White, new Rect(0, 0, size, size));
        for (var y = 0; y < rows.Count; y++)
        {
            var row = rows[y];
            for (var x = 0; x < row.Length; x++)
            {
                if (row[x] == '1')
                {
                    context.FillRectangle(Brushes.Black, new Rect((x + quiet) * cell, (y + quiet) * cell, cell, cell));
                }
            }
        }
    }
}
